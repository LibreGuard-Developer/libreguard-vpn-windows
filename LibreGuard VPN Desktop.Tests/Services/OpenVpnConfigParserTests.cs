using Xunit;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public class OpenVpnConfigParserTests
{
    private const string FullEmbeddedConfig = """
        client
        dev tun
        proto udp
        remote 185.199.108.42 1194
        resolv-retry infinite
        nobind
        persist-key
        persist-tun
        auth-user-pass
        <ca>
        -----BEGIN CERTIFICATE-----
        MIIB...test...
        -----END CERTIFICATE-----
        </ca>
        <cert>
        -----BEGIN CERTIFICATE-----
        MIIC...test...
        -----END CERTIFICATE-----
        </cert>
        <key>
        -----BEGIN PRIVATE KEY-----
        MIIEv...test...
        -----END PRIVATE KEY-----
        </key>
        <tls-auth>
        -----BEGIN OpenVPN Static key V1-----
        abcdef1234567890...
        -----END OpenVPN Static key V1-----
        </tls-auth>
        """;

    private const string ExternalCertConfig = """
        client
        dev tun
        proto tcp
        remote vpn.example.com 443
        ca /etc/openvpn/ca.crt
        cert /etc/openvpn/client.crt
        key /etc/openvpn/client.key
        tls-auth /etc/openvpn/ta.key 1
        """;

    private const string MinimalConfig = """
        client
        dev tun
        remote 10.0.0.1
        """;

    [Fact]
    public void HasEmbeddedCerts_WithCaAndCertAndKeyBlocks_ReturnsTrue()
    {
        var result = OpenVpnConfigParser.HasEmbeddedCerts(FullEmbeddedConfig);

        Assert.True(result);
    }

    [Fact]
    public void HasEmbeddedCerts_WithExternalFileRefs_ReturnsFalse()
    {
        var result = OpenVpnConfigParser.HasEmbeddedCerts(ExternalCertConfig);

        Assert.False(result);
    }

    [Fact]
    public void HasTlsAuth_WithInlineTlsAuthBlock_ReturnsTrue()
    {
        var result = OpenVpnConfigParser.HasTlsAuth(FullEmbeddedConfig);

        Assert.True(result);
    }

    [Fact]
    public void HasTlsAuth_WithExternalTlsAuthDirective_ReturnsTrue()
    {
        var result = OpenVpnConfigParser.HasTlsAuth(ExternalCertConfig);

        Assert.True(result);
    }

    [Fact]
    public void HasTlsAuth_WithNoTlsAuth_ReturnsFalse()
    {
        var result = OpenVpnConfigParser.HasTlsAuth(MinimalConfig);

        Assert.False(result);
    }

    [Fact]
    public void HasAuthUserPass_WithDirective_ReturnsTrue()
    {
        var result = OpenVpnConfigParser.HasAuthUserPass(FullEmbeddedConfig);

        Assert.True(result);
    }

    [Fact]
    public void HasAuthUserPass_WithoutDirective_ReturnsFalse()
    {
        var result = OpenVpnConfigParser.HasAuthUserPass(MinimalConfig);

        Assert.False(result);
    }

    [Fact]
    public void ValidateMinimalStructure_WithClientDevRemote_ReturnsTrue()
    {
        var result = OpenVpnConfigParser.ValidateMinimalStructure(MinimalConfig);

        Assert.True(result);
    }

    [Fact]
    public void ValidateMinimalStructure_MissingRemote_ReturnsFalse()
    {
        var configMissingRemote = """
            client
            dev tun
            """;

        var result = OpenVpnConfigParser.ValidateMinimalStructure(configMissingRemote);

        Assert.False(result);
    }

    [Fact]
    public void ValidateMinimalStructure_MissingClient_ReturnsFalse()
    {
        var configMissingClient = """
            dev tun
            remote 10.0.0.1
            """;

        var result = OpenVpnConfigParser.ValidateMinimalStructure(configMissingClient);

        Assert.False(result);
    }

    [Fact]
    public void ValidateMinimalStructure_EmptyString_ReturnsFalse()
    {
        var result = OpenVpnConfigParser.ValidateMinimalStructure("");

        Assert.False(result);
    }

    [Fact]
    public void ExtractRemoteHost_ValidConfig_ReturnsHost()
    {
        var result = OpenVpnConfigParser.ExtractRemoteHost(FullEmbeddedConfig);

        Assert.Equal("185.199.108.42", result);
    }

    [Fact]
    public void ExtractRemoteHost_HostnameConfig_ReturnsHostname()
    {
        var result = OpenVpnConfigParser.ExtractRemoteHost(ExternalCertConfig);

        Assert.Equal("vpn.example.com", result);
    }

    [Fact]
    public void ExtractRemoteHost_NoRemote_ReturnsNull()
    {
        var result = OpenVpnConfigParser.ExtractRemoteHost("client\ndev tun\n");

        Assert.Null(result);
    }

    [Fact]
    public void ExtractRemotePort_WithPort_ReturnsPort()
    {
        var result = OpenVpnConfigParser.ExtractRemotePort(FullEmbeddedConfig);

        Assert.Equal(1194, result);
    }

    [Fact]
    public void ExtractRemotePort_WithoutPort_ReturnsNull()
    {
        var result = OpenVpnConfigParser.ExtractRemotePort(MinimalConfig);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractProtocol_UdpConfig_ReturnsUdp()
    {
        var result = OpenVpnConfigParser.ExtractProtocol(FullEmbeddedConfig);

        Assert.Equal("udp", result);
    }

    [Fact]
    public void ExtractProtocol_TcpConfig_ReturnsTcp()
    {
        var result = OpenVpnConfigParser.ExtractProtocol(ExternalCertConfig);

        Assert.Equal("tcp", result);
    }

    [Fact]
    public void ExtractProtocol_NoProto_ReturnsNull()
    {
        var result = OpenVpnConfigParser.ExtractProtocol(MinimalConfig);

        Assert.Null(result);
    }

    [Fact]
    public void HasExternalCertReferences_WithFilePaths_ReturnsTrue()
    {
        var result = OpenVpnConfigParser.HasExternalCertReferences(ExternalCertConfig);

        Assert.True(result);
    }

    [Fact]
    public void HasExternalCertReferences_WithEmbeddedOnly_ReturnsFalse()
    {
        // Config with only inline blocks and no file-path directives
        var inlineOnly = """
            client
            dev tun
            remote 10.0.0.1 1194
            <ca>
            -----BEGIN CERTIFICATE-----
            test
            -----END CERTIFICATE-----
            </ca>
            <cert>
            -----BEGIN CERTIFICATE-----
            test
            -----END CERTIFICATE-----
            </cert>
            <key>
            -----BEGIN PRIVATE KEY-----
            test
            -----END PRIVATE KEY-----
            </key>
            """;

        var result = OpenVpnConfigParser.HasExternalCertReferences(inlineOnly);

        Assert.False(result);
    }

    [Fact]
    public void ExtractDeviceType_TunConfig_ReturnsTun()
    {
        var result = OpenVpnConfigParser.ExtractDeviceType(FullEmbeddedConfig);

        Assert.Equal("tun", result);
    }

    [Fact]
    public void ExtractDeviceType_NoDevDirective_ReturnsNull()
    {
        var result = OpenVpnConfigParser.ExtractDeviceType("client\nremote 10.0.0.1\n");

        Assert.Null(result);
    }

    [Fact]
    public void NormalizeForLaunch_RemovesPlaceholderAskpass()
    {
        var config = """
            client
            dev tun
            remote vpn.example.com 1194
            askpass [ENCRYPTED_PASSPHRASE]
            auth-user-pass
            """;

        var result = OpenVpnConfigParser.NormalizeForLaunch(config);

        Assert.DoesNotContain("askpass [ENCRYPTED_PASSPHRASE]", result);
        Assert.Contains("auth-user-pass", result);
        Assert.Contains("remote vpn.example.com 1194", result);
    }

    [Fact]
    public void NormalizeForLaunch_RemovesPlaceholderSetenvPassphrase()
    {
        var config = """
            client
            dev tun
            remote vpn.example.com 1194
            setenv CLIENT_PASSWORD [ENCRYPTED_PASSPHRASE]
            """;

        var result = OpenVpnConfigParser.NormalizeForLaunch(config);

        Assert.DoesNotContain("[ENCRYPTED_PASSPHRASE]", result);
        Assert.Contains("client", result);
        Assert.Contains("dev tun", result);
        Assert.Contains("remote vpn.example.com 1194", result);
    }

    [Fact]
    public void NormalizeForLaunch_PreservesRealDirectivesAndInlineCertificates()
    {
        var result = OpenVpnConfigParser.NormalizeForLaunch(FullEmbeddedConfig);

        Assert.Contains("auth-user-pass", result);
        Assert.Contains("<ca>", result);
        Assert.Contains("<cert>", result);
        Assert.Contains("<key>", result);
        Assert.Contains("<tls-auth>", result);
    }

    [Fact]
    public void ApplyPrivateDnsPolicy_ReplacesLegacyDnsAndConflictingPullFilters()
    {
        var config = """
            client
            dev tun
            remote vpn.example.com 1194
            dhcp-option DNS 1.1.1.1
              --DHCP-OPTION "DNS" 10.254.0.54
            dhcp-option DNS6 2606:4700:4700::1111
            dns server -1 address 9.9.9.9
            --dns server 1 address 10.254.0.54 2001:4860:4860::8888
            dns server 2 resolve-domains internal.example
            dns search-domains local.example
            --dns search-domains local2.example
            pull-filter accept "dhcp-option DNS"
            pull-filter reject 'dhcp-option DNS6'
            pull-filter ignore "dhcp-option"
            pull-filter accept "dhcp-option D"
            pull-filter accept "dns server"
            pull-filter accept "dn"
            pull-filter accept ""
            --pull-filter reject "dns "
            pull-filter ignore "dns search-domains"
            pull-filter ignore "route-ipv6"
            pull-filter ignore "dhcp-option WINS"
            dhcp-option WINS 192.0.2.10
            block-outside-dns
            --BLOCK-OUTSIDE-DNS
            """;

        var result = OpenVpnConfigParser.ApplyPrivateDnsPolicy(config);
        var activeLines = ActiveLinesOutsideInlineBlocks(result);

        Assert.Single(activeLines, line => line.Equals("dhcp-option DNS 10.254.0.53", StringComparison.OrdinalIgnoreCase));
        Assert.Single(activeLines, line => line.Equals("pull-filter ignore \"dhcp-option DNS\"", StringComparison.Ordinal));
        Assert.Single(activeLines, line => line.Equals("pull-filter ignore \"dhcp-option DNS6\"", StringComparison.Ordinal));
        Assert.Single(activeLines, line => line.Equals("pull-filter ignore \"dns \"", StringComparison.Ordinal));
        Assert.Single(activeLines, line => line.Equals("block-outside-dns", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(activeLines, line => line.Contains("10.254.0.54", StringComparison.Ordinal));
        Assert.DoesNotContain(activeLines, line => line.Contains("1.1.1.1", StringComparison.Ordinal));
        Assert.DoesNotContain(activeLines, line => line.Contains("2606:4700:4700::1111", StringComparison.Ordinal));
        Assert.DoesNotContain(activeLines, line => line.TrimStart('-').StartsWith("dns server", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("pull-filter accept \"dhcp-option D\"", activeLines);
        Assert.DoesNotContain("pull-filter accept \"dn\"", activeLines);
        Assert.DoesNotContain("pull-filter accept \"\"", activeLines);
        Assert.Contains("dns search-domains local.example", activeLines);
        Assert.Contains("--dns search-domains local2.example", activeLines);
        Assert.Contains("pull-filter ignore \"route-ipv6\"", activeLines);
        Assert.Contains("pull-filter ignore \"dhcp-option WINS\"", activeLines);
        Assert.Contains("dhcp-option WINS 192.0.2.10", activeLines);
    }

    [Fact]
    public void ApplyPrivateDnsPolicy_PreservesCommentsAndInlineCertificateContent()
    {
        var config = """
            client
            dev tun
            remote vpn.example.com 1194
            # dhcp-option DNS 10.254.0.54 is documentation only
            ; pull-filter accept "dhcp-option DNS"
            <ca>
            dhcp-option DNS 10.254.0.54
            pull-filter reject "dhcp-option DNS6"
            block-outside-dns
            -----BEGIN CERTIFICATE-----
            test
            -----END CERTIFICATE-----
            </ca>
            <key>
            unrelated-key-material
            </key>
            """;

        var result = OpenVpnConfigParser.ApplyPrivateDnsPolicy(config);

        Assert.Contains("# dhcp-option DNS 10.254.0.54 is documentation only", result);
        Assert.Contains("; pull-filter accept \"dhcp-option DNS\"", result);
        Assert.Contains("""
            <ca>
            dhcp-option DNS 10.254.0.54
            pull-filter reject "dhcp-option DNS6"
            block-outside-dns
            -----BEGIN CERTIFICATE-----
            test
            -----END CERTIFICATE-----
            </ca>
            """, result);
        Assert.Contains("""
            <key>
            unrelated-key-material
            </key>
            """, result);

        var activeLines = ActiveLinesOutsideInlineBlocks(result);
        Assert.Single(activeLines, line => line == "dhcp-option DNS 10.254.0.53");
        Assert.Single(activeLines, line => line == "block-outside-dns");
    }

    [Fact]
    public void ApplyPrivateDnsPolicy_IsIdempotentAndPreservesLineEndings()
    {
        const string config = "client\r\ndev tun\r\nremote vpn.example.com 1194\r\n\r\n";

        var once = OpenVpnConfigParser.ApplyPrivateDnsPolicy(config);
        var twice = OpenVpnConfigParser.ApplyPrivateDnsPolicy(once);

        Assert.Equal(once, twice);
        Assert.DoesNotContain("\n", once.Replace("\r\n", string.Empty));
        Assert.EndsWith("\r\n\r\n", once);
    }

    private static string[] ActiveLinesOutsideInlineBlocks(string config)
    {
        var activeLines = new List<string>();
        string? inlineBlock = null;

        foreach (var sourceLine in config.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = sourceLine.Trim();
            if (inlineBlock is not null)
            {
                if (line.Equals($"</{inlineBlock}>", StringComparison.OrdinalIgnoreCase))
                    inlineBlock = null;
                continue;
            }

            if (line.Length > 2 && line.StartsWith('<') && !line.StartsWith("</", StringComparison.Ordinal) && line.EndsWith('>'))
            {
                inlineBlock = line[1..^1];
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            activeLines.Add(line);
        }

        return [.. activeLines];
    }
}
