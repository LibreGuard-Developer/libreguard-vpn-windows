using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class CardCheckoutWebView2EnvironmentTests
{
    [Fact]
    public void GetUserDataFolder_UsesPerUserLibreGuardDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var result = CardCheckoutWebView2Environment.GetUserDataFolder();

        Assert.Equal(Path.Combine(localAppData, "LibreGuardVPN", "WebView2"), result);
        Assert.DoesNotContain("Program Files", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetUserDataFolder_UsesProvidedLocalAppDataRoot()
    {
        var result = CardCheckoutWebView2Environment.GetUserDataFolder(@"C:\Users\TestUser\AppData\Local");

        Assert.Equal(@"C:\Users\TestUser\AppData\Local\LibreGuardVPN\WebView2", result);
    }
}
