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
        Assert.Contains("Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0'", script);
        Assert.Contains("New-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -NextHop '0.0.0.0' -RouteMetric 1 -PolicyStore ActiveStore", script);
        Assert.Contains("$vpnDefaultRoutes | Set-NetRoute -RouteMetric 1", script);
        Assert.Contains("VPN full-tunnel verification failed", script);
        Assert.Contains("Add-DnsClientNrptRule -Namespace '.' -NameServers $DnsServers", script);
        Assert.Contains("LibreGuard VPN private DNS", script);
        Assert.Contains("VPN DNS policy verification failed", script);
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

    [Fact]
    public void BuildClearLibreGuardDnsPolicyScript_RemovesOnlyLibreGuardPolicy()
    {
        var script = VpnCommandHandler.BuildClearLibreGuardDnsPolicyScript();

        Assert.Contains("LibreGuard VPN private DNS", script);
        Assert.Contains("Get-DnsClientNrptRule", script);
        Assert.Contains("$_.Comment -eq $dnsPolicyComment", script);
        Assert.Contains("Remove-DnsClientNrptRule -Force", script);
        Assert.Contains("Clear-DnsClientCache", script);
    }

    [Fact]
    public void BuildCreateConnectionScript_DisablesAndVerifiesSplitTunneling()
    {
        var script = VpnCommandHandler.BuildCreateConnectionScript("LibreGuard VPN", "de-multi-2.libreguard.net");

        Assert.Contains("-SplitTunneling:$false", script);
        Assert.Contains("Set-VpnConnection -Name 'LibreGuard VPN' -SplitTunneling:$false -Force", script);
        Assert.Contains("Get-VpnConnection -Name 'LibreGuard VPN'", script);
        Assert.Contains("VPN profile unexpectedly has split tunneling enabled", script);
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
