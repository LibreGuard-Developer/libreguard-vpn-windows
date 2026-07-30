using LibreGuard.VpnService;
using Xunit;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public class OpenVpnHealthProbeTests
{
    [Fact]
    public void GetHealthCore_WhenExecutableMissing_RequiresSetup()
    {
        var health = OpenVpnProcessManager.GetHealthCore(
            resolveOpenVpnPath: () => null,
            isDriverInstalled: () => true);

        Assert.False(health.OpenVpnInstalled);
        Assert.True(health.OpenVpnDriverInstalled);
        Assert.Contains("not installed", health.SetupRequiredReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetHealthCore_WhenDriverMissing_RequiresDriverRepair()
    {
        var health = OpenVpnProcessManager.GetHealthCore(
            resolveOpenVpnPath: () => @"C:\ProgramData\LibreGuard VPN\Service\bin\openvpn.exe",
            isDriverInstalled: () => false);

        Assert.True(health.OpenVpnInstalled);
        Assert.False(health.OpenVpnDriverInstalled);
        Assert.Contains("driver", health.SetupRequiredReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetHealthCore_WhenExecutableAndDriverPresent_IsReady()
    {
        var health = OpenVpnProcessManager.GetHealthCore(
            resolveOpenVpnPath: () => @"C:\ProgramData\LibreGuard VPN\Service\bin\openvpn.exe",
            isDriverInstalled: () => true);

        Assert.True(health.OpenVpnInstalled);
        Assert.True(health.OpenVpnDriverInstalled);
        Assert.Null(health.SetupRequiredReason);
    }

    [Theory]
    [InlineData("tap0901")]
    [InlineData("tapwindows6")]
    [InlineData("ovpn-dco")]
    [InlineData("win-dco")]
    [InlineData("wintun")]
    public void IsKnownTapDriverComponentId_RecognizesSupportedDrivers(string componentId)
    {
        Assert.True(OpenVpnProcessManager.IsKnownTapDriverComponentId(componentId));
    }
}
