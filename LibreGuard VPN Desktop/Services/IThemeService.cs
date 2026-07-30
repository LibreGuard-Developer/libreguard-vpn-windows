using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

public interface IThemeService : IDisposable
{
    AppThemePreference CurrentPreference { get; }

    Task ApplyPreferenceAsync(AppThemePreference preference);
}
