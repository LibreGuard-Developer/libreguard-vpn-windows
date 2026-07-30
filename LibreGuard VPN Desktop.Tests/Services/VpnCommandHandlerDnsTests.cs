using LibreGuard.VpnService;
using Xunit;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class VpnCommandHandlerDnsTests
{
    [Fact]
    public void BuildSetDnsServersScript_PrefersAndVerifiesExplicitIpv4Interface()
    {
        var script = VpnCommandHandler.BuildSetDnsServersScript(
            "LibreGuard VPN",
            ["10.254.0.53"],
            29);

        Assert.Contains("$dnsServers = @('10.254.0.53')", script);
        Assert.Contains("$targetInterfaceIndex = 29", script);
        Assert.Contains(
            "Set-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -ServerAddresses $DnsServers -ErrorAction Stop",
            script);
        Assert.Contains(
            "Set-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 1 -ErrorAction Stop",
            script);
        Assert.Contains(
            "Get-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop",
            script);
        Assert.Contains(
            "Get-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop",
            script);
        Assert.Contains("VPN DNS verification failed", script);
        Assert.Contains("VPN DNS priority verification failed", script);
        Assert.Contains("Clear-DnsClientCache", script);
        Assert.DoesNotContain("10.254.0.54", script);
        Assert.DoesNotContain("1.1.1.1", script);
    }

    [Fact]
    public void BuildSetDnsServersScript_FallbackLookupAppliesSameVerifiedPolicy()
    {
        var script = VpnCommandHandler.BuildSetDnsServersScript(
            "LibreGuard VPN",
            ["10.254.0.53"],
            interfaceIndex: null);

        Assert.Contains("$targetInterfaceIndex = $null", script);
        Assert.Contains(
            "Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4",
            script);
        Assert.Contains(
            "Set-LibreGuardDnsPolicy -InterfaceIndex $dnsClient.InterfaceIndex -DnsServers $dnsServers",
            script);
        Assert.Equal(2, CountOccurrences(script, "Set-LibreGuardDnsPolicy -InterfaceIndex"));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;

        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }
}
