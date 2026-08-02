using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace LibreGuard.VpnService;

/// <summary>
/// Executes privileged VPN operations (cert import, VPN entry management, IPsec config, rasdial).
/// Runs inside the Windows Service process (LocalSystem) — no UAC prompts needed.
/// </summary>
internal sealed class VpnCommandHandler
{
    private const string LibreGuardDnsPolicyComment = "LibreGuard VPN private DNS";
    private static readonly string[] ApprovedTrustedRootThumbprints =
    [
        "A9571557A77DB78FFAC2E97B57B898569039C340", // Root YE
        "C5F111DA84F7DEF8E6F3F99F8F5F36FF85BAB1B1"  // Root YR
    ];
    private readonly ILogger<VpnCommandHandler> _logger;

    public VpnCommandHandler(ILogger<VpnCommandHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Imports PKCS#12 certificates into LocalMachine stores.
    /// Client cert ? LocalMachine\My, CA cert ? LocalMachine\Root.
    /// Returns (clientThumbprint, caThumbprint).
    /// </summary>
    public (string? ClientThumbprint, string? CaThumbprint) ImportCertificates(byte[] pfxBytes, string? password)
    {
        // Remove stale IKEV2_client certs first
        RemoveStaleClientCerts();

        // Use the modern loader that supports PBES2/AES-256-CBC (OpenSSL 3.x default)
        var collection = X509CertificateLoader.LoadPkcs12Collection(
            pfxBytes, password,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);

        _logger.LogInformation("P12 bundle contains {Count} certificate(s)", collection.Count);

        string? clientThumbprint = null;
        string? caThumbprint = null;

        foreach (var cert in collection)
        {
            LogCertificateDetails(cert, "P12");

            if (cert.HasPrivateKey)
            {
                using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
                store.Close();
                clientThumbprint = cert.Thumbprint;
                _logger.LogInformation("Client cert imported: {Thumbprint}", clientThumbprint);
                LogStorePresence(StoreName.My, clientThumbprint, "client");
            }
            else
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
                store.Close();
                caThumbprint = cert.Thumbprint;
                _logger.LogInformation("CA cert imported: {Thumbprint}", caThumbprint);
                LogStorePresence(StoreName.Root, caThumbprint, "CA");
            }
        }

        return (clientThumbprint, caThumbprint);
    }

    /// <summary>
    /// Imports a tightly allow-listed public root CA into LocalMachine\Root.
    /// Used for the tightly pinned Let's Encrypt Generation Y roots, which are not yet
    /// present in all OS trust stores.
    /// </summary>
    public string ImportTrustedRootCertificate(byte[] certificateBytes)
    {
        using var cert = X509CertificateLoader.LoadCertificate(certificateBytes);
        LogCertificateDetails(cert, "TrustedRoot");
        ValidateTrustedRootCertificate(cert);

        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
        if (existing.Count > 0)
        {
            _logger.LogInformation("Trusted root already present: {Thumbprint}", cert.Thumbprint);
            store.Close();
            LogStorePresence(StoreName.Root, cert.Thumbprint, "trusted root");
            return cert.Thumbprint;
        }

        store.Add(cert);
        store.Close();

        _logger.LogInformation("Trusted root imported: {Thumbprint}", cert.Thumbprint);
        LogStorePresence(StoreName.Root, cert.Thumbprint, "trusted root");
        return cert.Thumbprint;
    }

    /// <summary>
    /// Creates (or recreates) the IKEv2 VPN connection with MachineCertificate auth.
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> CreateConnectionAsync(
        string connectionName, string serverAddress, CancellationToken ct)
    {
        return await RunPowerShellAsync(BuildCreateConnectionScript(connectionName, serverAddress), ct);
    }

    internal static string BuildCreateConnectionScript(string connectionName, string serverAddress)
    {
        return string.Join("\n",
            $"Remove-VpnConnection -Name '{connectionName}' -Force -ErrorAction SilentlyContinue",
            "",
            $"Add-VpnConnection -Name '{connectionName}' `",
            $"    -ServerAddress '{serverAddress}' `",
            "    -TunnelType Ikev2 `",
            "    -AuthenticationMethod MachineCertificate `",
            "    -EncryptionLevel Required `",
            "    -SplitTunneling:$false `",
            "    -RememberCredential `",
            "    -Force",
            "",
            // Do not rely on the cmdlet default: the RAS profile must explicitly retain
            // the remote default gateway setting before we start the connection.
            $"Set-VpnConnection -Name '{connectionName}' -SplitTunneling:$false -Force -ErrorAction Stop | Out-Null",
            $"$vpnProfile = Get-VpnConnection -Name '{connectionName}' -ErrorAction Stop",
            "if ($vpnProfile.SplitTunneling) { throw 'VPN profile unexpectedly has split tunneling enabled.' }");
    }

    /// <summary>
    /// Sets the IPsec policy for the VPN connection to match StrongSwan cipher suites.
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> SetIpsecPolicyAsync(
        string connectionName, CancellationToken ct)
    {
        var script = $"Set-VpnConnectionIPsecConfiguration -ConnectionName '{connectionName}' " +
                     "-AuthenticationTransformConstants SHA256128 " +
                     "-CipherTransformConstants AES256 " +
                     "-DHGroup Group14 " +
                     "-EncryptionMethod AES256 " +
                     "-IntegrityCheckMethod SHA256 " +
                     "-PfsGroup None " +
                     "-Force";

        return await RunPowerShellAsync(script, ct);
    }

    /// <summary>
    /// Applies DNS servers to the active VPN interface.
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> SetDnsServersAsync(
        string connectionName, IReadOnlyCollection<string> dnsServers, int? interfaceIndex, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(dnsServers);

        if (dnsServers.Count == 0)
            throw new ArgumentException("At least one DNS server is required.", nameof(dnsServers));

        var script = BuildSetDnsServersScript(connectionName, dnsServers, interfaceIndex);

        return await RunPowerShellAsync(script, ct);
    }

    internal static string BuildSetDnsServersScript(
        string connectionName, IReadOnlyCollection<string> dnsServers, int? interfaceIndex)
    {
        var dnsArray = string.Join(", ", dnsServers.Select(QuotePowerShellString));
        var interfaceIndexValue = interfaceIndex.HasValue
            ? interfaceIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "$null";
        return string.Join("\n",
            $"$connectionName = {QuotePowerShellString(connectionName)}",
            $"$dnsServers = @({dnsArray})",
            $"$targetInterfaceIndex = {interfaceIndexValue}",
            "$adapter = $null",
            "$dnsClient = $null",
            "function Set-LibreGuardDnsPolicy {",
            "    param([uint32]$InterfaceIndex, [string[]]$DnsServers)",
            "    Set-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -ServerAddresses $DnsServers -ErrorAction Stop | Out-Null",
            "    Set-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 1 -ErrorAction Stop | Out-Null",
            "    Clear-DnsClientCache -ErrorAction SilentlyContinue",
            "    $configuredDns = @((Get-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop).ServerAddresses)",
            "    if (($configuredDns -join ',') -ne ($DnsServers -join ',')) {",
            "        throw \"VPN DNS verification failed on interface '$InterfaceIndex'. Expected '$($DnsServers -join ',')', found '$($configuredDns -join ',')'.\"",
            "    }",
            "    $configuredInterface = Get-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop | Select-Object -First 1",
            "    if (-not $configuredInterface -or [int]$configuredInterface.InterfaceMetric -ne 1) {",
            "        $actualMetric = if ($configuredInterface) { $configuredInterface.InterfaceMetric } else { '<missing>' }",
            "        throw \"VPN DNS priority verification failed on interface '$InterfaceIndex'. Expected IPv4 interface metric 1, found '$actualMetric'.\"",
            "    }",
            "    # A connected RAS session is not sufficient: the selected default route must",
            "    # actually belong to the VPN interface. Some Windows/IKEv2 combinations leave",
            "    # the physical adapter's default route preferred despite SplitTunneling being false.",
            "    $vpnDefaultRoutes = @(Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue)",
            "    if ($vpnDefaultRoutes.Count -eq 0) {",
            "        New-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -NextHop '0.0.0.0' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null",
            "        $vpnDefaultRoutes = @(Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop)",
            "    }",
            "    $vpnDefaultRoutes | Set-NetRoute -RouteMetric 1 -ErrorAction Stop",
            "    $bestDefaultRoute = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop | Sort-Object @{ Expression = { [int]$_.RouteMetric + [int]$_.InterfaceMetric } }, InterfaceIndex | Select-Object -First 1",
            "    if (-not $bestDefaultRoute -or [int]$bestDefaultRoute.InterfaceIndex -ne [int]$InterfaceIndex) {",
            "        $actualInterface = if ($bestDefaultRoute) { $bestDefaultRoute.InterfaceIndex } else { '<missing>' }",
            "        throw \"VPN full-tunnel verification failed. Expected the preferred IPv4 default route on interface '$InterfaceIndex', found '$actualInterface'.\"",
            "    }",
            "    # Interface DNS alone is insufficient on multi-homed Windows hosts: the DNS client can",
            "    # otherwise select a resolver from a physical adapter. A catch-all NRPT rule pins all",
            "    # normal DNS resolution to the VPN resolver while this tunnel is active.",
            $"    $dnsPolicyComment = {QuotePowerShellString(LibreGuardDnsPolicyComment)}",
            "    Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -eq $dnsPolicyComment } | Remove-DnsClientNrptRule -Force -ErrorAction Stop",
            "    $nrptRule = Add-DnsClientNrptRule -Namespace '.' -NameServers $DnsServers -Comment $dnsPolicyComment -DisplayName $dnsPolicyComment -PassThru -ErrorAction Stop",
            "    $configuredNrpt = @($nrptRule.NameServers)",
            "    if (($configuredNrpt -join ',') -ne ($DnsServers -join ',')) {",
            "        throw \"VPN DNS policy verification failed. Expected '$($DnsServers -join ',')', found '$($configuredNrpt -join ',')'.\"",
            "    }",
            "    Get-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 | Select-Object InterfaceAlias, InterfaceIndex, AddressFamily, ServerAddresses | Format-Table -AutoSize | Out-String",
            "    $configuredInterface | Select-Object InterfaceAlias, InterfaceIndex, AddressFamily, InterfaceMetric | Format-Table -AutoSize | Out-String",
            "    $bestDefaultRoute | Select-Object InterfaceAlias, InterfaceIndex, DestinationPrefix, NextHop, RouteMetric, InterfaceMetric | Format-Table -AutoSize | Out-String",
            "    $nrptRule | Select-Object Namespace, NameServers, Comment | Format-Table -AutoSize | Out-String",
            "}",
            "if ($targetInterfaceIndex) {",
            "    for ($i = 0; $i -lt 30; $i++) {",
            "        $dnsClient = Get-DnsClientServerAddress -InterfaceIndex $targetInterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1",
            "        if ($dnsClient) { break }",
            "        Start-Sleep -Milliseconds 500",
            "    }",
            "    if (-not $dnsClient) { throw \"VPN interface index '$targetInterfaceIndex' did not expose DNS client settings for '$connectionName'.\" }",
            "    Set-LibreGuardDnsPolicy -InterfaceIndex $targetInterfaceIndex -DnsServers $dnsServers",
            "    return",
            "}",
            "for ($i = 0; $i -lt 30; $i++) {",
            "    $adapter = Get-NetAdapter -Name $connectionName -IncludeHidden -ErrorAction SilentlyContinue | Select-Object -First 1",
            "    if (-not $adapter) {",
            "        $adapter = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -eq $connectionName -or $_.InterfaceDescription -like \"*$connectionName*\" } | Select-Object -First 1",
            "    }",
            "    if ($adapter -and $adapter.Status -eq 'Up') {",
            "        $dnsClient = Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1",
            "        if ($dnsClient) { break }",
            "    }",
            "    Start-Sleep -Milliseconds 500",
            "}",
            "if (-not $adapter) {",
            "    $available = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Select-Object -First 20 -ExpandProperty Name",
            "    throw \"VPN interface '$connectionName' not found for DNS configuration. Available adapters: $($available -join ', ')\"",
            "}",
            "if ($adapter.Status -ne 'Up') { throw \"VPN interface '$connectionName' was found but is not up for DNS configuration. Status: $($adapter.Status), InterfaceIndex: $($adapter.ifIndex)\" }",
            "if (-not $dnsClient) { throw \"VPN interface '$connectionName' did not expose DNS client settings. InterfaceIndex: $($adapter.ifIndex)\" }",
            "Set-LibreGuardDnsPolicy -InterfaceIndex $dnsClient.InterfaceIndex -DnsServers $dnsServers");
    }

    /// <summary>
    /// Removes the temporary catch-all NRPT rule used to bind DNS resolution to the VPN.
    /// This is intentionally independent of adapter cleanup so stale rules are also removed
    /// after a crash or an interrupted connection attempt.
    /// </summary>
    public async Task ClearLibreGuardDnsPolicyAsync(CancellationToken ct)
    {
        var (exitCode, output, error) = await RunPowerShellAsync(BuildClearLibreGuardDnsPolicyScript(), ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to remove LibreGuard DNS policy: {error} {output}".Trim());
    }

    internal static string BuildClearLibreGuardDnsPolicyScript()
    {
        return string.Join("\n",
            $"$dnsPolicyComment = {QuotePowerShellString(LibreGuardDnsPolicyComment)}",
            "Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -eq $dnsPolicyComment } | Remove-DnsClientNrptRule -Force -ErrorAction Stop",
            "Clear-DnsClientCache -ErrorAction SilentlyContinue");
    }

    /// <summary>
    /// Connects via rasdial.
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> DialAsync(
        string connectionName, CancellationToken ct)
    {
        return await RunProcessAsync("rasdial", $"\"{connectionName}\"", ct);
    }

    /// <summary>
    /// Disconnects via rasdial and removes the VPN entry.
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> DisconnectAsync(
        string connectionName, CancellationToken ct)
    {
        var (rasdialExitCode, rasdialOutput, rasdialError) = await RunProcessAsync(
            "rasdial", $"\"{connectionName}\" /disconnect", ct);
        var (removeExitCode, removeOutput, removeError) = await RunPowerShellAsync(
            $"Remove-VpnConnection -Name '{connectionName}' -Force -ErrorAction SilentlyContinue", ct);

        var output = string.Join(Environment.NewLine,
            new[]
            {
                $"rasdial /disconnect exit {rasdialExitCode}: {rasdialOutput}".Trim(),
                $"Remove-VpnConnection exit {removeExitCode}: {removeOutput}".Trim()
            }.Where(line => !line.EndsWith(":", StringComparison.Ordinal)));
        var error = string.Join(Environment.NewLine,
            new[]
            {
                $"rasdial /disconnect error: {rasdialError}".Trim(),
                $"Remove-VpnConnection error: {removeError}".Trim()
            }.Where(line => !line.EndsWith(":", StringComparison.Ordinal)));

        return (removeExitCode, output, error);
    }

    public async Task<bool> IsConnectionActiveAsync(string connectionName, CancellationToken ct)
    {
        var (_, output, error) = await RunProcessAsync("rasdial", string.Empty, ct);
        var combined = $"{output} {error}";
        return combined.Contains(connectionName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(bool Active, int ExitCode, string Output)> DisconnectAndVerifyAsync(string connectionName, CancellationToken ct)
    {
        var (exitCode, output, error) = await DisconnectAsync(connectionName, ct);
        var (statusExitCode, statusOutput, statusError) = await RunProcessAsync("rasdial", string.Empty, ct);
        var active = $"{statusOutput} {statusError}".Contains(connectionName, StringComparison.OrdinalIgnoreCase);
        var detail = string.Join(Environment.NewLine,
            new[]
            {
                output,
                error,
                $"rasdial status exit {statusExitCode}: {statusOutput}".Trim(),
                string.IsNullOrWhiteSpace(statusError) ? null : $"rasdial status error: {statusError}"
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(detail))
            detail = $"rasdial/remove exit {exitCode}";

        return (active, exitCode, detail);
    }

    /// <summary>
    /// Removes the VPN entry without disconnecting rasdial.
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> RemoveConnectionAsync(
        string connectionName, CancellationToken ct)
    {
        return await RunPowerShellAsync(
            $"Remove-VpnConnection -Name '{connectionName}' -Force -ErrorAction SilentlyContinue", ct);
    }

    /// <summary>
    /// Removes specific certificates from LocalMachine stores (best-effort cleanup on failure).
    /// </summary>
    public void CleanupCertificates(string? clientThumbprint, string? caThumbprint)
    {
        if (clientThumbprint is not null)
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                var certs = store.Certificates.Find(X509FindType.FindByThumbprint, clientThumbprint, false);
                foreach (var cert in certs)
                    store.Remove(cert);
                store.Close();
                _logger.LogInformation("Removed client cert: {Thumbprint}", clientThumbprint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove client cert {Thumbprint}", clientThumbprint);
            }
        }

        if (caThumbprint is not null)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                var certs = store.Certificates.Find(X509FindType.FindByThumbprint, caThumbprint, false);
                foreach (var cert in certs)
                    store.Remove(cert);
                store.Close();
                _logger.LogInformation("Removed CA cert: {Thumbprint}", caThumbprint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove CA cert {Thumbprint}", caThumbprint);
            }
        }
    }

    private void RemoveStaleClientCerts()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var staleCerts = store.Certificates
                .Where(c => c.Subject.Contains("IKEV2_client", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var cert in staleCerts)
            {
                _logger.LogInformation("Removing stale cert: {Thumbprint}", cert.Thumbprint);
                store.Remove(cert);
            }

            store.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove stale IKEV2 client certs");
        }
    }

    private void LogCertificateDetails(X509Certificate2 cert, string source)
    {
        _logger.LogInformation(
            "{Source} cert: Subject={Subject}, Issuer={Issuer}, Thumbprint={Thumbprint}, Serial={Serial}, HasPrivateKey={HasPrivateKey}",
            source, cert.Subject, cert.Issuer, cert.Thumbprint, cert.SerialNumber, cert.HasPrivateKey);
        _logger.LogInformation(
            "{Source} cert validity: NotBefore={NotBefore:O}, NotAfter={NotAfter:O}, Signature={Signature}, PublicKey={PublicKey}",
            source, cert.NotBefore, cert.NotAfter,
            cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value,
            cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value);

        foreach (var extension in cert.Extensions)
            LogCertificateExtension(extension, source);
    }

    internal static void ValidateTrustedRootCertificate(X509Certificate2 cert)
    {
        var thumbprint = NormalizeThumbprint(cert.Thumbprint);
        if (!ApprovedTrustedRootThumbprints.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported trusted root certificate: {cert.Thumbprint}");

        if (cert.HasPrivateKey)
            throw new InvalidOperationException("Trusted root certificate must not include a private key.");

        var basicConstraints = cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basicConstraints?.CertificateAuthority != true)
            throw new InvalidOperationException("Trusted root certificate must be a certificate authority.");
    }

    private static string NormalizeThumbprint(string? thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint)
            ? string.Empty
            : thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal);

    private void LogCertificateExtension(X509Extension extension, string source)
    {
        switch (extension)
        {
            case X509BasicConstraintsExtension basic:
                _logger.LogInformation(
                    "{Source} extension BasicConstraints: CA={CertificateAuthority}, HasPathLength={HasPathLength}, PathLength={PathLength}",
                    source, basic.CertificateAuthority, basic.HasPathLengthConstraint, basic.PathLengthConstraint);
                break;

            case X509KeyUsageExtension keyUsage:
                _logger.LogInformation("{Source} extension KeyUsage: {KeyUsage}", source, keyUsage.KeyUsages);
                break;

            case X509EnhancedKeyUsageExtension enhancedKeyUsage:
                var ekuValues = enhancedKeyUsage.EnhancedKeyUsages
                    .Cast<System.Security.Cryptography.Oid>()
                    .Select(oid => $"{oid.FriendlyName ?? oid.Value} ({oid.Value})");
                _logger.LogInformation("{Source} extension EKU: {EnhancedKeyUsage}", source, string.Join(", ", ekuValues));
                break;

            default:
                _logger.LogInformation(
                    "{Source} extension {ExtensionName}: {ExtensionValue}",
                    source,
                    extension.Oid?.FriendlyName ?? extension.Oid?.Value ?? "(unknown)",
                    extension.Format(multiLine: false));
                break;
        }
    }

    private void LogStorePresence(StoreName storeName, string? thumbprint, string purpose)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return;

        try
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            _logger.LogInformation(
                "{Purpose} certificate store verification: Store=LocalMachine\\{StoreName}, Thumbprint={Thumbprint}, Matches={Matches}",
                purpose, storeName, thumbprint, certs.Count);
            store.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to verify {Purpose} certificate in LocalMachine\\{StoreName}: {Thumbprint}",
                purpose, storeName, thumbprint);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellAsync(
        string script, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"lg_svc_{Guid.NewGuid():N}.ps1");
        try
        {
            var fullScript = $"$ErrorActionPreference = 'Stop'\n{script}\n";
            await File.WriteAllTextAsync(scriptPath, fullScript, ct);

            return await RunProcessAsync(
                "powershell",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                ct);
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                try { File.Delete(scriptPath); }
                catch { /* best-effort */ }
            }
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string QuotePowerShellString(string value) => $"'{value.Replace("'", "''")}'";

    public async Task EnableKillSwitchAsync(string? vpnServerIp, string? vpnLocalIp, CancellationToken ct = default)
    {
        _logger.LogInformation("Enabling Kill Switch. VPN Server IP: {VpnServerIp}, VPN Local IP: {VpnLocalIp}", vpnServerIp, vpnLocalIp);

        // 1. Remove existing Kill Switch rules
        await DisableKillSwitchAsync(ct);

        // 2. Add Block rules for all outbound traffic
        await RunProcessAsync("netsh", "advfirewall firewall add rule name=\"LG-KS-Block-v4\" dir=out action=block protocol=any profile=any", ct);
        await RunProcessAsync("netsh", "advfirewall firewall add rule name=\"LG-KS-Block-v6\" dir=out action=block protocol=any profile=any remoteip=::/0", ct);

        // 3. Allow outbound to VPN Server IP
        if (!string.IsNullOrWhiteSpace(vpnServerIp))
        {
            await RunProcessAsync("netsh", $"advfirewall firewall add rule name=\"LG-KS-Allow-Server\" dir=out action=allow remoteip={vpnServerIp}", ct);
        }

        // 4. Allow outbound from VPN Local IP (if connected)
        if (!string.IsNullOrWhiteSpace(vpnLocalIp))
        {
            await RunProcessAsync("netsh", $"advfirewall firewall add rule name=\"LG-KS-Allow-Tunnel\" dir=out action=allow localip={vpnLocalIp}", ct);
        }
        else
        {
            // Fallback for IKEv2 if local IP is not known yet
            await RunProcessAsync("netsh", "advfirewall firewall add rule name=\"LG-KS-Allow-Tunnel\" dir=out action=allow interfacetype=ras", ct);
        }

        // 5. Allow DHCP and DNS (optional, but usually needed for local network resolution before VPN connects)
        // We allow DHCP to get local IP. DNS is allowed to the VPN DNS if connected, but if disconnected, we might need local DNS to resolve the VPN server hostname.
        // If vpnServerIp is an IP address, we don't strictly need local DNS.
        await RunProcessAsync("netsh", "advfirewall firewall add rule name=\"LG-KS-Allow-DHCP\" dir=out action=allow protocol=udp remoteport=67,68", ct);
        await RunProcessAsync("netsh", "advfirewall firewall add rule name=\"LG-KS-Allow-DNS\" dir=out action=allow protocol=udp remoteport=53", ct);
        await RunProcessAsync("netsh", "advfirewall firewall add rule name=\"LG-KS-Allow-DNS-TCP\" dir=out action=allow protocol=tcp remoteport=53", ct);

        // 6. Allow Localhost
        await RunProcessAsync("netsh", "advfirewall firewall add rule name=\"LG-KS-Allow-Localhost\" dir=out action=allow remoteip=127.0.0.0/8,::1", ct);
    }

    public async Task DisableKillSwitchAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Disabling Kill Switch.");
        await RunProcessAsync("netsh", "advfirewall firewall delete rule name=\"LG-KS-Block-v4\"", ct);
        await RunProcessAsync("netsh", "advfirewall firewall delete rule name=\"LG-KS-Block-v6\"", ct);
        await RunProcessAsync("netsh", "advfirewall firewall delete rule name=\"LG-KS-Allow-Server\"", ct);
        await RunProcessAsync("netsh", "advfirewall firewall delete rule name=\"LG-KS-Allow-Tunnel\"", ct);
        await RunProcessAsync("netsh", "advfirewall firewall delete rule name=\"LG-KS-Allow-DHCP\"", ct);
        await RunProcessAsync("netsh", "advfirewall firewall delete rule name=\"LG-KS-Allow-DNS\"", ct);
        await RunProcessAsync("netsh", "advfirewall firewall delete rule name=\"LG-KS-Allow-DNS-TCP\"", ct);
        await RunProcessAsync("netsh", "advfirewall firewall delete rule name=\"LG-KS-Allow-Localhost\"", ct);
    }
}

