using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class ThemeServiceTests
{
    [Fact]
    public void ResolveEffectiveTheme_LightPreference_UsesLight()
    {
        using var service = new ThemeService(new StaticSystemThemeReader(false));

        Assert.Equal(AppTheme.Light, service.ResolveEffectiveTheme(AppThemePreference.Light));
    }

    [Fact]
    public void ResolveEffectiveTheme_DarkPreference_UsesDark()
    {
        using var service = new ThemeService(new StaticSystemThemeReader(true));

        Assert.Equal(AppTheme.Dark, service.ResolveEffectiveTheme(AppThemePreference.Dark));
    }

    [Fact]
    public void ResolveEffectiveTheme_SystemPreference_UsesSystemThemeReader()
    {
        using var lightService = new ThemeService(new StaticSystemThemeReader(true));
        using var darkService = new ThemeService(new StaticSystemThemeReader(false));

        Assert.Equal(AppTheme.Light, lightService.ResolveEffectiveTheme(AppThemePreference.System));
        Assert.Equal(AppTheme.Dark, darkService.ResolveEffectiveTheme(AppThemePreference.System));
    }

    private sealed class StaticSystemThemeReader : ISystemThemeReader
    {
        private readonly bool _isLightThemeEnabled;

        public StaticSystemThemeReader(bool isLightThemeEnabled)
        {
            _isLightThemeEnabled = isLightThemeEnabled;
        }

        public bool IsLightThemeEnabled() => _isLightThemeEnabled;
    }
}
