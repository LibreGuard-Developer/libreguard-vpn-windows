namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Displays Windows toast notifications for VPN lifecycle events.
/// All methods are no-ops when the user has disabled notifications in settings.
/// </summary>
public interface INotificationService
{
    /// <summary>Notify that the VPN tunnel is currently being established.</summary>
    void NotifyVpnConnecting();

    /// <summary>Notify that the VPN tunnel came up successfully.</summary>
    void NotifyVpnConnected(string serverName, string city, string country, string? ipAddress);

    /// <summary>Notify that the VPN tunnel has been disconnected.</summary>
    void NotifyVpnDisconnected();

    /// <summary>Notify that an active VPN connection was unexpectedly lost.</summary>
    void NotifyConnectionLost();

    /// <summary>Notify that a connection attempt failed with an actionable message.</summary>
    void NotifyConnectionError(string message);

    /// <summary>Notify that the Kill Switch has been activated and is blocking traffic.</summary>
    void NotifyKillSwitchEnabled();

    /// <summary>Notify that the Kill Switch has been deactivated and normal traffic is restored.</summary>
    void NotifyKillSwitchDisabled();

    /// <summary>Notify that monthly data usage has crossed the 80 % warning threshold.</summary>
    void NotifyDataUsageWarning(double percentUsed);

    /// <summary>Notify that the monthly data limit has been fully consumed.</summary>
    void NotifyDataLimitReached();
}
