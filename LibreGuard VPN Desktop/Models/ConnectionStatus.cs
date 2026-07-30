namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Represents the current state of the VPN connection.
/// </summary>
public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Reconnecting,
    Error
}
