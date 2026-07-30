using LibreGuard.VpnService;
using LibreGuard_VPN_Desktop.Services;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public class IKEv2TunnelStrategyTests
{
    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_ForceDisconnectsAndVerifiesAllTunnels()
    {
        var client = new RecordingVpnServiceClient();
        var strategy = new IKEv2TunnelStrategy(new CertificateCacheService(), client);

        await strategy.DisconnectAsync();

        var command = Assert.Single(client.Requests);
        Assert.Equal(VpnCommandType.ForceDisconnectAll, command.Command);
        Assert.Equal("LibreGuard VPN", command.ConnectionName);
    }

    [Fact]
    public async Task DisconnectAsync_WhenServiceFails_Throws()
    {
        var client = new RecordingVpnServiceClient
        {
            Response = new VpnServiceResponse
            {
                Success = false,
                ErrorMessage = "rasdial failed"
            }
        };
        var strategy = new IKEv2TunnelStrategy(new CertificateCacheService(), client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.DisconnectAsync());

        Assert.Contains("rasdial failed", ex.Message);
    }

    [Fact]
    public async Task DisconnectAsync_WhenTunnelRemainsActive_ThrowsVerificationFailure()
    {
        var client = new RecordingVpnServiceClient
        {
            Response = new VpnServiceResponse
            {
                Success = true,
                TunnelActive = true,
                TunnelStatus = "OpenVPN=Disconnected; IKEv2=Connected"
            }
        };
        var strategy = new IKEv2TunnelStrategy(new CertificateCacheService(), client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.DisconnectAsync());

        Assert.Contains("IKEv2=Connected", ex.Message);
    }

    [Fact]
    public void ParseStrongSwanConfig_ReadsDnsServers()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sswan");
        var payload = """
            {
              "uuid": "abc",
              "name": "LibreGuard",
              "type": "ikev2-cert",
              "remote": {
                "addr": "198.51.100.10",
                "id": "vpn.example.com",
                "cert": "CERT",
                "ike": "aes256-sha256-modp2048",
                "esp": "aes256-sha256"
              },
              "local": {
                "p12": "UEs=",
                "password": "secret"
              },
              "dns-servers": ["10.0.0.53", "1.1.1.1"]
            }
            """;

        File.WriteAllText(tempPath, payload);

        try
        {
            var config = IKEv2TunnelStrategy.ParseStrongSwanConfig(tempPath);

            Assert.Equal(["10.0.0.53", "1.1.1.1"], config.DnsServers);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public async Task ApplyPrivateDnsPolicyAsync_AlwaysSetsOnlyInternalResolver()
    {
        var client = new RecordingVpnServiceClient();
        var strategy = new IKEv2TunnelStrategy(new CertificateCacheService(), client);

        await strategy.ApplyPrivateDnsPolicyAsync(42);

        var request = Assert.Single(client.Requests);
        Assert.Equal(VpnCommandType.SetDnsServers, request.Command);
        Assert.Equal("LibreGuard VPN", request.ConnectionName);
        Assert.Equal(["10.254.0.53"], request.DnsServers!);
        Assert.Equal(42, request.VpnInterfaceIndex);
        Assert.DoesNotContain("10.254.0.54", request.DnsServers!);
    }

    [Fact]
    public async Task ApplyPrivateDnsPolicyAsync_WhenDnsSetupFails_ForceDisconnectsBeforeRemovingConnection()
    {
        var client = new SequencedVpnServiceClient(
            new VpnServiceResponse { Success = false, ErrorMessage = "adapter rejected DNS" },
            new VpnServiceResponse { Success = true },
            new VpnServiceResponse { Success = true });
        var strategy = new IKEv2TunnelStrategy(new CertificateCacheService(), client);
        using var connectCts = new CancellationTokenSource();
        connectCts.Cancel();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.ApplyPrivateDnsPolicyAsync(7, connectCts.Token));

        Assert.Contains("private DNS configuration failed", ex.Message);
        Assert.Contains("adapter rejected DNS", ex.Message);
        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal(VpnCommandType.SetDnsServers, request.Command);
                Assert.Equal(["10.254.0.53"], request.DnsServers!);
                Assert.Equal(7, request.VpnInterfaceIndex);
            },
            request => Assert.Equal(VpnCommandType.ForceDisconnectAll, request.Command),
            request => Assert.Equal(VpnCommandType.RemoveConnection, request.Command));
        Assert.Equal(connectCts.Token, client.CancellationTokens[0]);
        Assert.Equal(CancellationToken.None, client.CancellationTokens[1]);
        Assert.Equal(CancellationToken.None, client.CancellationTokens[2]);
    }

    [Fact]
    public async Task ApplyPrivateDnsPolicyAsync_WhenForcedTeardownReportsActive_ThrowsTeardownFailureAfterCleanup()
    {
        var client = new SequencedVpnServiceClient(
            new VpnServiceResponse { Success = false, ErrorMessage = "adapter rejected DNS" },
            new VpnServiceResponse
            {
                Success = false,
                TunnelActive = true,
                ErrorMessage = "IKEv2 tunnel is still active"
            },
            new VpnServiceResponse { Success = true });
        var strategy = new IKEv2TunnelStrategy(new CertificateCacheService(), client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.ApplyPrivateDnsPolicyAsync(7));

        Assert.Contains("adapter rejected DNS", ex.Message);
        Assert.Contains("Forced VPN teardown could not be verified", ex.Message);
        Assert.Contains("could not verify forced tunnel teardown", ex.Message);
        Assert.Equal(
            [VpnCommandType.SetDnsServers, VpnCommandType.ForceDisconnectAll, VpnCommandType.RemoveConnection],
            client.Requests.Select(request => request.Command));
    }

    [Theory]
    [InlineData("Root YE", "A9571557A77DB78FFAC2E97B57B898569039C340", "E14FFCAD5B0025731006CAA43A121A22D8E9700F4FB9CF852F02A708AA5D5666")]
    [InlineData("Root YR", "C5F111DA84F7DEF8E6F3F99F8F5F36FF85BAB1B1", "E57B7E6F150C419102E8D5C055729FF967B9D1A829BF00CEC89CA604EBF4A86F")]
    public void LoadBundledTrustedRootCertificates_LoadsExpectedRoot(
        string rootName,
        string expectedSha1,
        string expectedSha256)
    {
        var root = Assert.Single(
            IKEv2TunnelStrategy.LoadBundledTrustedRootCertificates(),
            candidate => candidate.Name == rootName);
        var certBytes = Convert.FromBase64String(root.CertificateBase64);
        using var cert = X509CertificateLoader.LoadCertificate(certBytes);

        Assert.Equal($"CN={rootName}, O=ISRG, C=US", cert.Subject);
        Assert.Equal(cert.Subject, cert.Issuer);
        Assert.Equal(expectedSha1, cert.Thumbprint);
        Assert.Equal(expectedSha256, cert.GetCertHashString(HashAlgorithmName.SHA256));
        Assert.False(cert.HasPrivateKey);
        Assert.Contains(
            cert.Extensions.OfType<X509BasicConstraintsExtension>(),
            extension => extension.CertificateAuthority);
    }

    [Fact]
    public async Task EnsureBundledTrustedRootsAsync_ImportsBothApprovedRootsInOrder()
    {
        var client = new RecordingVpnServiceClient
        {
            Response = new VpnServiceResponse
            {
                Success = true,
                TrustedRootThumbprint = "already-installed"
            }
        };
        var strategy = new IKEv2TunnelStrategy(new CertificateCacheService(), client);

        await strategy.EnsureBundledTrustedRootsAsync();

        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, request => Assert.Equal(VpnCommandType.ImportTrustedRootCertificate, request.Command));
        Assert.Equal(
            ["CN=Root YE, O=ISRG, C=US", "CN=Root YR, O=ISRG, C=US"],
            client.Requests.Select(GetTrustedRootSubject));
    }

    [Theory]
    [InlineData(0, "Root YE", 1)]
    [InlineData(1, "Root YR", 2)]
    public async Task EnsureBundledTrustedRootsAsync_WhenImportFails_StopsWithRootSpecificError(
        int failureIndex,
        string expectedRootName,
        int expectedRequestCount)
    {
        var client = new SequencedVpnServiceClient(
            Enumerable.Range(0, failureIndex)
                .Select(_ => new VpnServiceResponse { Success = true })
                .Append(new VpnServiceResponse { Success = false, ErrorMessage = "denied" })
                .ToArray());
        var strategy = new IKEv2TunnelStrategy(new CertificateCacheService(), client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.EnsureBundledTrustedRootsAsync());

        Assert.Contains(expectedRootName, ex.Message);
        Assert.Contains("denied", ex.Message);
        Assert.Equal(expectedRequestCount, client.Requests.Count);
    }

    [Theory]
    [InlineData("Root YE")]
    [InlineData("Root YR")]
    public void ValidateTrustedRootCertificate_AcceptsApprovedBundledRoots(string rootName)
    {
        var root = Assert.Single(
            IKEv2TunnelStrategy.LoadBundledTrustedRootCertificates(),
            candidate => candidate.Name == rootName);
        using var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(root.CertificateBase64));

        VpnCommandHandler.ValidateTrustedRootCertificate(cert);
    }

    [Fact]
    public void ValidateTrustedRootCertificate_RejectsUnapprovedCertificateAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Unapproved Test Root",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var ex = Assert.Throws<InvalidOperationException>(
            () => VpnCommandHandler.ValidateTrustedRootCertificate(cert));

        Assert.Contains("Unsupported trusted root certificate", ex.Message);
    }

    [Fact]
    public void FindUsableVpnInterface_ReturnsUpLibreGuardInterfaceWithIpv4()
    {
        var result = IKEv2TunnelStrategy.FindUsableVpnInterface(
        [
            new IKEv2TunnelStrategy.VpnInterfaceSnapshot("Ethernet", "Ethernet", OperationalStatus.Up, "192.168.1.20"),
            new IKEv2TunnelStrategy.VpnInterfaceSnapshot("LibreGuard VPN", "WAN Miniport (IKEv2)", OperationalStatus.Up, "10.10.0.2")
        ]);

        Assert.NotNull(result);
        Assert.Equal("10.10.0.2", result.Ipv4Address);
    }

    [Fact]
    public void FindUsableVpnInterface_MatchesDescriptionWhenAliasIsDifferent()
    {
        var result = IKEv2TunnelStrategy.FindUsableVpnInterface(
        [
            new IKEv2TunnelStrategy.VpnInterfaceSnapshot("VPN Adapter", "LibreGuard VPN", OperationalStatus.Up, "10.10.0.2")
        ]);

        Assert.NotNull(result);
    }

    [Fact]
    public void FindUsableVpnInterface_IgnoresDownOrAddresslessInterfaces()
    {
        var result = IKEv2TunnelStrategy.FindUsableVpnInterface(
        [
            new IKEv2TunnelStrategy.VpnInterfaceSnapshot("LibreGuard VPN", "LibreGuard VPN", OperationalStatus.Down, "10.10.0.2"),
            new IKEv2TunnelStrategy.VpnInterfaceSnapshot("LibreGuard VPN", "LibreGuard VPN", OperationalStatus.Up, null)
        ]);

        Assert.Null(result);
    }

    private static string GetTrustedRootSubject(VpnServiceRequest request)
    {
        var certBytes = Convert.FromBase64String(Assert.IsType<string>(request.TrustedRootCertificateBase64));
        using var cert = X509CertificateLoader.LoadCertificate(certBytes);
        return cert.Subject;
    }

    private sealed class SequencedVpnServiceClient(params VpnServiceResponse[] responses) : IVpnServiceClient
    {
        private readonly Queue<VpnServiceResponse> _responses = new(responses);

        public List<VpnServiceRequest> Requests { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<VpnServiceResponse> SendAsync(VpnServiceRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            CancellationTokens.Add(ct);
            return Task.FromResult(_responses.Dequeue());
        }

        public Task<bool> IsServiceAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
