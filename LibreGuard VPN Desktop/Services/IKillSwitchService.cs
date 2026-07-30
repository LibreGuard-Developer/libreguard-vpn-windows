using System.Threading.Tasks;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Manages the system-level Kill Switch to prevent IP/DNS leaks when the VPN disconnects.
/// </summary>
public interface IKillSwitchService
{
    /// <summary>
    /// Gets whether the Kill Switch is currently enabled by the user.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Enables the Kill Switch. Blocks all traffic except to the specified VPN server IP.
    /// </summary>
    /// <param name="vpnServerIp">The IP address of the VPN server to allow.</param>
    /// <param name="vpnLocalIp">The local IP address of the VPN interface (if connected).</param>
    Task EnableAsync(string? vpnServerIp = null, string? vpnLocalIp = null);

    /// <summary>
    /// Disables the Kill Switch, restoring normal internet access.
    /// </summary>
    Task DisableAsync();
}
