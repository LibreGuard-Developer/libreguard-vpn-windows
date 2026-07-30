using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Establishes an IKEv2/IPSec VPN tunnel using the Windows built-in VPN client.
/// Delegates all privileged operations (cert import, VPN entry, IPsec, rasdial) to the
/// LibreGuard VPN Service over a named pipe — zero UAC prompts at runtime.
/// Falls back to elevated PowerShell if the service is unavailable.
/// </summary>
internal sealed class IKEv2TunnelStrategy : IVpnTunnelStrategy
{
    private const string VpnConnectionName = "LibreGuard VPN";
    private static readonly (string Name, string ResourceName)[] BundledTrustedRootResources =
    [
        ("Root YE", "LibreGuard.Roots.root_ye.pem"),
        ("Root YR", "LibreGuard.Roots.root_yr.pem")
    ];
    private readonly CertificateCacheService _certCache;
    private readonly IVpnServiceClient _serviceClient;
    private bool _isConnected;
    private string? _importedCertThumbprint;
    private string? _importedCaThumbprint;
    private string? _localIp;

    public VpnProtocol Protocol => VpnProtocol.IKEv2;

    public bool IsConnected => _isConnected && TryGetUsableVpnInterface() is not null;

    public long BytesIn
    {
        get
        {
            if (!_isConnected) return 0;
            try
            {
                var ni = TryGetUsableVpnInterface();
                return ni?.GetIPStatistics().BytesReceived ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public long BytesOut
    {
        get
        {
            if (!_isConnected) return 0;
            try
            {
                var ni = TryGetUsableVpnInterface();
                return ni?.GetIPStatistics().BytesSent ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public string? LocalIp
    {
        get
        {
            return IsConnected ? _localIp : null;
        }
    }

    public IKEv2TunnelStrategy(CertificateCacheService certCache, IVpnServiceClient serviceClient)
    {
        ArgumentNullException.ThrowIfNull(certCache);
        ArgumentNullException.ThrowIfNull(serviceClient);
        _certCache = certCache;
        _serviceClient = serviceClient;
    }

    public async Task ConnectAsync(string configPath, string? passphrase, string serverIp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serverIp);

        await DisconnectAsync(ct);

        // 1. Parse the StrongSwan .sswan JSON config
        var sswanConfig = ParseStrongSwanConfig(configPath);

        Debug.WriteLine($"[IKEv2] Remote config: addr={sswanConfig.Remote.Addr}, id={sswanConfig.Remote.Id}, " +
                        $"cert.length={(sswanConfig.Remote.Cert?.Length ?? 0)}, ike={sswanConfig.Remote.Ike}, esp={sswanConfig.Remote.Esp}");
        if (!string.IsNullOrWhiteSpace(sswanConfig.Remote.Cert))
            LogRemoteCertificate(sswanConfig.Remote.Cert);

        // 2. Import certificates (skipped if cached)
        var configHash = ConfigHashUtility.GenerateHashFromFile(configPath);
        var certThumbprint = await ImportCertificatesAsync(sswanConfig, passphrase, configHash, ct);
        _importedCertThumbprint = certThumbprint;

        // Windows IKEv2 validates the VPN server certificate through LocalMachine\Root.
        // Let's Encrypt Generation Y chains can terminate at roots that are not yet present
        // in every OS trust store, so install the tightly allow-listed bundled trust anchors.
        await EnsureBundledTrustedRootsAsync(ct);

        var connectAddress = !string.IsNullOrEmpty(sswanConfig.Remote.Addr)
            ? sswanConfig.Remote.Addr
            : serverIp;

        // 3. Disconnect any active rasdial session (may be left over from crash/restart)
        await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.Disconnect,
            ConnectionName = VpnConnectionName
        }, ct);

        // 4. Create the IKEv2 VPN connection with MachineCertificate auth
        Debug.WriteLine($"[IKEv2] Creating VPN connection to '{connectAddress}' with MachineCertificate auth");
        var createResult = await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.CreateConnection,
            ConnectionName = VpnConnectionName,
            ServerAddress = connectAddress
        }, ct);

        if (!createResult.Success)
            throw new InvalidOperationException($"Failed to create VPN connection: {createResult.ErrorMessage}");

        Debug.WriteLine("[IKEv2] VPN connection created with MachineCertificate auth");

        // 5. Configure IPsec policy
        Debug.WriteLine("[IKEv2] Setting IPsec policy");
        var ipsecResult = await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.SetIpsecPolicy,
            ConnectionName = VpnConnectionName
        }, ct);

        if (!ipsecResult.Success)
            throw new InvalidOperationException($"Failed to set IPsec policy: {ipsecResult.ErrorMessage}");

        // 6. Connect using rasdial
        Debug.WriteLine("[IKEv2] Initiating rasdial connection");
        var dialResult = await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.Dial,
            ConnectionName = VpnConnectionName
        }, ct);

        if (!dialResult.Success)
        {
            await CleanupConnectionAsync(ct);
            throw new InvalidOperationException(
                $"IKEv2 connection failed (exit code {dialResult.ExitCode}): {dialResult.Output}");
        }

        var verifiedInterface = await WaitForUsableVpnInterfaceAsync(ct);
        if (verifiedInterface is null)
        {
            await CleanupConnectionAsync(ct);
            throw new InvalidOperationException(
                "IKEv2 connected, but Windows did not expose a usable LibreGuard VPN interface.");
        }

        _localIp = verifiedInterface.Ipv4Address;

        await ApplyPrivateDnsPolicyAsync(verifiedInterface.InterfaceIndex, ct);

        _isConnected = true;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var response = await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.ForceDisconnectAll,
            ConnectionName = VpnConnectionName
        }, ct);

        if (!response.Success || response.TunnelActive)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { response.ErrorMessage, response.TunnelStatus, response.Output }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "Failed to verify that the IKEv2 tunnel was disconnected."
                : detail);
        }

        _isConnected = false;
        _localIp = null;
    }

    /// <summary>
    /// Parses the StrongSwan .sswan JSON configuration from the saved config file.
    /// </summary>
    internal static StrongSwanConfig ParseStrongSwanConfig(string configPath)
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<StrongSwanConfig>(json)
            ?? throw new InvalidOperationException("Failed to parse StrongSwan configuration.");

        if (string.IsNullOrEmpty(config.Local.P12))
            throw new InvalidOperationException("StrongSwan config is missing the PKCS#12 certificate data.");

        return config;
    }

    internal async Task ApplyPrivateDnsPolicyAsync(int? vpnInterfaceIndex, CancellationToken ct = default)
    {
        var dnsServers = VpnDnsPolicy.CreateResolverList();
        Debug.WriteLine($"[IKEv2] Applying mandatory private DNS server: {dnsServers[0]}");

        try
        {
            var dnsResult = await _serviceClient.SendAsync(new VpnServiceRequest
            {
                Command = VpnCommandType.SetDnsServers,
                ConnectionName = VpnConnectionName,
                DnsServers = dnsServers,
                VpnInterfaceIndex = vpnInterfaceIndex
            }, ct);

            if (dnsResult.Success)
                return;

            throw new InvalidOperationException(
                $"VPN connected, but private DNS configuration failed: {dnsResult.ErrorMessage ?? dnsResult.Output}");
        }
        catch (Exception dnsException)
        {
            // DNS is mandatory. Do not leave a live tunnel behind when Windows cannot apply it,
            // even when the caller's connection token was cancelled during DNS setup.
            try
            {
                await TearDownAfterDnsFailureAsync();
            }
            catch (Exception teardownException)
            {
                throw new InvalidOperationException(
                    $"{dnsException.Message} Forced VPN teardown could not be verified: {teardownException.Message}",
                    new AggregateException(dnsException, teardownException));
            }

            throw;
        }
    }

    private async Task TearDownAfterDnsFailureAsync()
    {
        Exception? forceDisconnectFailure = null;
        try
        {
            var disconnectResult = await _serviceClient.SendAsync(new VpnServiceRequest
            {
                Command = VpnCommandType.ForceDisconnectAll,
                ConnectionName = VpnConnectionName
            }, CancellationToken.None);

            if (!disconnectResult.Success || disconnectResult.TunnelActive)
            {
                forceDisconnectFailure = new InvalidOperationException(
                    disconnectResult.ErrorMessage ??
                    disconnectResult.TunnelStatus ??
                    "The VPN service reported that a tunnel remains active.");
            }
        }
        catch (Exception disconnectException)
        {
            forceDisconnectFailure = disconnectException;
        }

        try
        {
            await CleanupConnectionAsync(CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            Debug.WriteLine($"[IKEv2] Cleanup after DNS configuration failure also failed: {cleanupException.Message}");
            _isConnected = false;
            _localIp = null;
        }

        if (forceDisconnectFailure is not null)
            throw new InvalidOperationException("The VPN service could not verify forced tunnel teardown.", forceDisconnectFailure);
    }

    internal sealed record VpnInterfaceSnapshot(
        string Name,
        string Description,
        OperationalStatus OperationalStatus,
        string? Ipv4Address,
        int? InterfaceIndex = null);

    internal static VpnInterfaceSnapshot? FindUsableVpnInterface(IEnumerable<VpnInterfaceSnapshot> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);

        return interfaces.FirstOrDefault(candidate =>
            IsLibreGuardInterface(candidate.Name, candidate.Description) &&
            candidate.OperationalStatus == OperationalStatus.Up &&
            !string.IsNullOrWhiteSpace(candidate.Ipv4Address));
    }

    private static bool IsLibreGuardInterface(string? name, string? description)
    {
        return string.Equals(name, VpnConnectionName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(description, VpnConnectionName, StringComparison.OrdinalIgnoreCase) ||
               (description?.Contains(VpnConnectionName, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static IEnumerable<VpnInterfaceSnapshot> GetVpnInterfaceSnapshots()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            string? ipv4 = null;
            int? interfaceIndex = null;
            try
            {
                var properties = ni.GetIPProperties();
                ipv4 = properties.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.Address
                    .ToString();
                interfaceIndex = properties.GetIPv4Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
                // The interface can disappear while Windows is still settling RAS state.
            }

            yield return new VpnInterfaceSnapshot(ni.Name, ni.Description, ni.OperationalStatus, ipv4, interfaceIndex);
        }
    }

    private static NetworkInterface? TryGetUsableVpnInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni =>
                {
                    if (!IsLibreGuardInterface(ni.Name, ni.Description) ||
                        ni.OperationalStatus != OperationalStatus.Up)
                    {
                        return false;
                    }

                    try
                    {
                        return ni.GetIPProperties()
                            .UnicastAddresses
                            .Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    }
                    catch (NetworkInformationException)
                    {
                        return false;
                    }
                });
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static async Task<VpnInterfaceSnapshot?> WaitForUsableVpnInterfaceAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            var verified = FindUsableVpnInterface(GetVpnInterfaceSnapshots());
            if (verified is not null)
                return verified;

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        return FindUsableVpnInterface(GetVpnInterfaceSnapshots());
    }

    /// <summary>
    /// Imports the PKCS#12 client certificate and CA into LocalMachine stores via the VPN service.
    /// Uses <see cref="CertificateCacheService"/> to skip the import when certificates are still
    /// present in the store. Returns the thumbprint of the client certificate.
    /// </summary>
    private async Task<string> ImportCertificatesAsync(StrongSwanConfig config, string? passphrase, string configHash, CancellationToken ct)
    {
        // Check cache first — skip service call if certs are still valid in the store
        var cached = _certCache.TryGetCachedCertificates(configHash);
        if (cached is { WasValid: true, ClientThumbprint: not null })
        {
            Debug.WriteLine($"[IKEv2] Using cached certificates (client={cached.ClientThumbprint})");
            _importedCertThumbprint = cached.ClientThumbprint;
            _importedCaThumbprint = cached.CaThumbprint;
            return cached.ClientThumbprint;
        }

        Debug.WriteLine("[IKEv2] Certificate cache miss — importing via service");

        // Prefer passphrase if provided (decrypted from storage), otherwise fallback to the one in the config.
        // Convert empty string password to null for better compatibility with no-password PFX files.
        var pfxPassword = !string.IsNullOrEmpty(passphrase) ? passphrase : config.Local.Password;
        if (string.IsNullOrEmpty(pfxPassword)) pfxPassword = null;

        var originalPfxBytes = Convert.FromBase64String(config.Local.P12);

        // Ensure the P12 is in a format .NET can read.
        // OpenSSL 3.x uses UTF-8 password encoding for PKCS#12 MAC/KDF, but .NET
        // only supports BMPString encoding. BouncyCastle handles both, so we re-export
        // through it when .NET's loader fails.
        var pfxBytes = EnsureCompatiblePkcs12(originalPfxBytes, pfxPassword, out var workingPassword);
        var pfxBase64 = ReferenceEquals(pfxBytes, originalPfxBytes)
            ? config.Local.P12
            : Convert.ToBase64String(pfxBytes);

        // Diagnostic: verify bundle contents using the working password
        var collection = X509CertificateLoader.LoadPkcs12Collection(
            pfxBytes, workingPassword, X509KeyStorageFlags.DefaultKeySet);
        Debug.WriteLine($"[IKEv2] P12 bundle contains {collection.Count} certificate(s):");

        foreach (var c in collection)
            LogCertificateDetails(c, "P12");

        // Delegate the actual import to the elevated service
        var response = await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.ImportCertificates,
            PfxBase64 = pfxBase64,
            PfxPassword = workingPassword
        }, ct);

        if (!response.Success || response.ClientThumbprint is null)
            throw new InvalidOperationException($"Certificate import failed: {response.ErrorMessage}");

        _importedCertThumbprint = response.ClientThumbprint;
        _importedCaThumbprint = response.CaThumbprint;

        // Cache the thumbprints so subsequent connections skip the service call
        _certCache.CacheCertificates(configHash, response.ClientThumbprint, response.CaThumbprint);

        Debug.WriteLine($"[IKEv2] Client cert thumbprint: {response.ClientThumbprint}");
        Debug.WriteLine($"[IKEv2] CA cert thumbprint: {response.CaThumbprint ?? "(none in bundle)"}");

        return response.ClientThumbprint;
    }

    /// <summary>
    /// Ensures the PKCS#12 bytes can be loaded by .NET. OpenSSL 3.x creates P12 files
    /// with UTF-8 password encoding for MAC/KDF, which .NET does not support (it uses
    /// BMPString per the original PKCS#12 spec). When .NET's loader fails, BouncyCastle
    /// (which supports both encodings) re-exports with legacy-compatible settings.
    /// </summary>
    private static byte[] EnsureCompatiblePkcs12(byte[] pfxBytes, string? password, out string? workingPassword)
    {
        try
        {
            // Fast path: .NET 9+ can usually read modern P12s unless there's a MAC/digest mismatch.
            // We use DefaultKeySet here for the probe.
            var probe = X509CertificateLoader.LoadPkcs12Collection(
                pfxBytes, password, X509KeyStorageFlags.DefaultKeySet);
            foreach (var cert in probe)
                cert.Dispose();
            
            workingPassword = password;
            return pfxBytes;
        }
        catch (CryptographicException ex)
        {
            Debug.WriteLine($"[IKEv2] .NET loader failed (reason: {ex.Message}) — falling back to BouncyCastle repair");
        }

        // BouncyCastle repair loop. We try: 
        // 1. The provided password
        // 2. An empty password (empty char array)
        // Note: We avoid passing 'null' to BC's Load() as it throws ArgumentNullException.
        var attempts = new List<(string? Str, char[] Chars)>();
        if (password != null) attempts.Add((password, password.ToCharArray()));
        attempts.Add(("", Array.Empty<char>()));

        var distinctAttempts = attempts.DistinctBy(x => x.Str).ToList();
        for (int i = 0; i < distinctAttempts.Count; i++)
        {
            var p = distinctAttempts[i];
            bool isLast = i == distinctAttempts.Count - 1;

            try
            {
                // We use Pkcs12StoreBuilder to ensure we're using default settings (supports UTF-8 and BMPString)
                var store = new Pkcs12StoreBuilder().Build();
                using (var input = new MemoryStream(pfxBytes))
                    store.Load(input, p.Chars);

                // Re-export the store to a format .NET is guaranteed to understand (legacy BMPString password encoding)
                using var output = new MemoryStream();
                store.Save(output, p.Chars, new SecureRandom());
                
                Debug.WriteLine($"[IKEv2] P12 re-exported successfully via BouncyCastle (pwd source={(string.IsNullOrEmpty(p.Str) ? "empty" : "provided")})");
                workingPassword = p.Str;
                return output.ToArray();
            }
            catch (Exception ex) when (!isLast && (ex is IOException or ArgumentException or CryptographicException))
            {
                Debug.WriteLine($"[IKEv2] BouncyCastle attempt failed (pwd source={(string.IsNullOrEmpty(p.Str) ? "empty" : "provided")}): {ex.Message}");
                continue;
            }
        }

        throw new IOException("Failed to unlock PKCS12 store. The password may be incorrect or the file corrupted.");
    }

    private static void LogRemoteCertificate(string remoteCert)
    {
        try
        {
            var certBytes = DecodeCertificateBytes(remoteCert);
            using var cert = X509CertificateLoader.LoadCertificate(certBytes);
            LogCertificateDetails(cert, "Remote");
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            Debug.WriteLine($"[IKEv2] Remote certificate could not be decoded: {ex.Message}");
        }
    }

    private static byte[] DecodeCertificateBytes(string certificate)
    {
        var trimmed = certificate.Trim();
        if (trimmed.Contains("-----BEGIN CERTIFICATE-----", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new StringBuilder();
            using var reader = new StringReader(trimmed);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.StartsWith("-----", StringComparison.Ordinal))
                    builder.Append(line.Trim());
            }

            return Convert.FromBase64String(builder.ToString());
        }

        return Convert.FromBase64String(trimmed);
    }

    internal async Task EnsureBundledTrustedRootsAsync(CancellationToken ct = default)
    {
        foreach (var root in LoadBundledTrustedRootCertificates())
        {
            Debug.WriteLine($"[IKEv2] Ensuring {root.Name} trust anchor is present");

            var response = await _serviceClient.SendAsync(new VpnServiceRequest
            {
                Command = VpnCommandType.ImportTrustedRootCertificate,
                TrustedRootCertificateBase64 = root.CertificateBase64
            }, ct);

            if (!response.Success)
                throw new InvalidOperationException($"Failed to install {root.Name} trust anchor: {response.ErrorMessage}");

            Debug.WriteLine($"[IKEv2] {root.Name} trust anchor ready: {response.TrustedRootThumbprint ?? "(unknown)"}");
        }
    }

    internal static IReadOnlyList<BundledTrustedRootCertificate> LoadBundledTrustedRootCertificates()
    {
        return BundledTrustedRootResources
            .Select(root => new BundledTrustedRootCertificate(
                root.Name,
                LoadBundledCertificateBase64(root.ResourceName)))
            .ToArray();
    }

    private static string LoadBundledCertificateBase64(string resourceName)
    {
        using var stream = typeof(IKEv2TunnelStrategy).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Bundled certificate resource not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var pem = reader.ReadToEnd();
        return Convert.ToBase64String(DecodeCertificateBytes(pem));
    }

    internal sealed record BundledTrustedRootCertificate(string Name, string CertificateBase64);

    private static void LogCertificateDetails(X509Certificate2 cert, string source)
    {
        Debug.WriteLine($"[IKEv2] {source} cert: Subject={cert.Subject}, Issuer={cert.Issuer}, " +
                        $"Thumbprint={cert.Thumbprint}, Serial={cert.SerialNumber}, HasPrivateKey={cert.HasPrivateKey}");
        Debug.WriteLine($"[IKEv2] {source} cert validity: NotBefore={cert.NotBefore:O}, NotAfter={cert.NotAfter:O}, " +
                        $"Signature={cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value}, " +
                        $"PublicKey={cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value}");

        foreach (var extension in cert.Extensions)
            LogCertificateExtension(extension, source);
    }

    private static void LogCertificateExtension(X509Extension extension, string source)
    {
        switch (extension)
        {
            case X509BasicConstraintsExtension basic:
                Debug.WriteLine($"[IKEv2] {source} extension BasicConstraints: CA={basic.CertificateAuthority}, " +
                                $"HasPathLength={basic.HasPathLengthConstraint}, PathLength={basic.PathLengthConstraint}");
                break;

            case X509KeyUsageExtension keyUsage:
                Debug.WriteLine($"[IKEv2] {source} extension KeyUsage: {keyUsage.KeyUsages}");
                break;

            case X509EnhancedKeyUsageExtension enhancedKeyUsage:
                var ekuValues = enhancedKeyUsage.EnhancedKeyUsages
                    .Cast<Oid>()
                    .Select(oid => $"{oid.FriendlyName ?? oid.Value} ({oid.Value})");
                Debug.WriteLine($"[IKEv2] {source} extension EKU: {string.Join(", ", ekuValues)}");
                break;

            default:
                var formatted = extension.Format(multiLine: false);
                Debug.WriteLine($"[IKEv2] {source} extension {extension.Oid?.FriendlyName ?? extension.Oid?.Value}: " +
                                $"{formatted}");
                break;
        }
    }

    /// <summary>
    /// Removes the VPN connection entry and attempts best-effort certificate cleanup.
    /// Called only on connection failure.
    /// </summary>
    private async Task CleanupConnectionAsync(CancellationToken ct)
    {
        await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.RemoveConnection,
            ConnectionName = VpnConnectionName
        }, ct);

        if (_importedCertThumbprint is not null || _importedCaThumbprint is not null)
        {
            try
            {
                await _serviceClient.SendAsync(new VpnServiceRequest
                {
                    Command = VpnCommandType.CleanupCertificates,
                    ClientThumbprint = _importedCertThumbprint,
                    CaThumbprint = _importedCaThumbprint
                }, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IKEv2] Cert cleanup failed (best-effort): {ex.Message}");
            }

            _importedCertThumbprint = null;
            _importedCaThumbprint = null;
        }

        _isConnected = false;
        _localIp = null;
    }

}

