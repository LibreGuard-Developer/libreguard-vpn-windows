using System;

namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Persisted user preferences for the application.
/// </summary>
public sealed class UserSettings
{
    public VpnProtocol DefaultProtocol { get; set; } = VpnProtocol.IKEv2;
    public AppThemePreference ThemePreference { get; set; } = AppThemePreference.System;
    
    // Future settings can be added here
    public bool AutoConnect { get; set; } = true;
    public bool KillSwitch { get; set; } = false;
    public bool Notifications { get; set; } = true;
}
