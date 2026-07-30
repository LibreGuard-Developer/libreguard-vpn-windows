using System.Runtime.ExceptionServices;
using System.Threading;
using LibreGuard.Common.Windows;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class TrayShellTests
{
    [Fact]
    public void TrayTooltipBuilder_WhenDisconnected_UsesExpectedText()
    {
        var text = TrayTooltipBuilder.Build(new TrayTooltipState(
            ConnectionStatus.Disconnected,
            Country: null,
            City: null,
            IpAddress: null,
            SessionDataMb: 0,
            Plan: UserPlan.Free,
            MonthlyDataUsedMb: 0,
            MonthlyDataLimitMb: 0));

        Assert.Equal("LibreGuard VPN - Not Connected", text);
    }

    [Fact]
    public void TrayTooltipBuilder_WhenConnected_FreePlanIncludesMonthlyUsageFallback()
    {
        var text = TrayTooltipBuilder.Build(new TrayTooltipState(
            ConnectionStatus.Connected,
            Country: "Germany",
            City: "Berlin",
            IpAddress: "10.8.0.5",
            SessionDataMb: 512,
            Plan: UserPlan.Free,
            MonthlyDataUsedMb: 2048,
            MonthlyDataLimitMb: -1));

        Assert.Contains("Berlin", text);
        Assert.Contains("10.8.0.5", text);
        Assert.Contains("512MB", text);
        Assert.Contains("2.0/5.0GB", text);
        Assert.True(text.Length <= TrayTooltipBuilder.MaxTextLength);
    }

    [Fact]
    public void TrayMenuBuilder_WhenFreePlan_DisablesPremiumServers()
    {
        var germany = new ServerCountryGroup(
            "Germany",
            "DE",
            null,
            [
                new ServerLocation("1", "Germany", "Berlin", "DE-Berlin-01", "DE", null, 10, 20),
                new ServerLocation("2", "Germany", "Frankfurt", "DE-Frankfurt-01", "DE", null, 12, 25, isPremium: true)
            ]);
        var menu = TrayMenuBuilder.Build(new TrayMenuBuildState(
            IsAuthenticated: true,
            CanQuickConnect: true,
            IsConnected: false,
            IsPro: false,
            Countries: [germany]));

        var quickConnect = Assert.IsType<TrayMenuEntry>(menu[0]);
        var servers = Assert.IsType<TrayMenuEntry>(menu[1]);
        var germanyEntry = Assert.Single(servers.Children!);
        Assert.True(quickConnect.Enabled);
        Assert.Equal("Servers", servers.Text);
        Assert.Equal("DE Germany", germanyEntry.Text);
        Assert.Equal(2, germanyEntry.Children!.Count);
        Assert.True(germanyEntry.Children[0].Enabled);
        Assert.False(germanyEntry.Children[1].Enabled);
        Assert.Contains("(Pro)", germanyEntry.Children[1].Text);
    }

    [Fact]
    public void TrayMenuBuilder_WhenUnauthenticated_DisablesActions()
    {
        var menu = TrayMenuBuilder.Build(new TrayMenuBuildState(
            IsAuthenticated: false,
            CanQuickConnect: false,
            IsConnected: false,
            IsPro: false,
            Countries: []));

        Assert.False(menu[0].Enabled);
        Assert.False(menu[1].Enabled);
        Assert.Equal("Exit", menu[3].Text);
    }

    [Fact]
    public void TrayMenuBuilder_WhenConnected_ShowsDisconnectAndDisablesServers()
    {
        var menu = TrayMenuBuilder.Build(new TrayMenuBuildState(
            IsAuthenticated: true,
            CanQuickConnect: true,
            IsConnected: true,
            IsPro: true,
            Countries:
            [
                new ServerCountryGroup(
                    "Germany",
                    "DE",
                    null,
                    [new ServerLocation("1", "Germany", "Berlin", "DE-Berlin-01", "DE", null, 10, 20)])
            ]));

        Assert.Equal("Disconnect", menu[0].Text);
        Assert.True(menu[0].Enabled);
        Assert.Equal(TrayMenuActionKind.Disconnect, menu[0].Action?.Kind);
        Assert.Equal("Servers", menu[1].Text);
        Assert.False(menu[1].Enabled);
    }

    [Fact]
    public void ShellLinkUtility_CreateOrUpdateShortcut_PersistsAppUserModelId()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var shortcutPath = Path.Combine(tempDirectory, "LibreGuard VPN.lnk");
        var targetPath = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path.");

        try
        {
            RunInSta(() =>
            {
                ShellLinkUtility.CreateOrUpdateShortcut(
                    shortcutPath,
                    targetPath,
                    Path.GetDirectoryName(targetPath)!,
                    targetPath,
                    AppIdentity.AppUserModelId);
                return 0;
            });

            Assert.True(File.Exists(shortcutPath));
            var aumid = RunInSta(() => ShellLinkUtility.ReadAppUserModelId(shortcutPath));
            if (aumid is not null)
                Assert.Equal(AppIdentity.AppUserModelId, aumid);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static T RunInSta<T>(Func<T> action)
    {
        Exception? failure = null;
        T? result = default;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return result!;
    }
}
