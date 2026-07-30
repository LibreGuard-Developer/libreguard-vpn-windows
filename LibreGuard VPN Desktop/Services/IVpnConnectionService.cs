using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Manages VPN tunnel lifecycle: connect, disconnect, and status monitoring.
/// </summary>
public interface IVpnConnectionService
{
    /// <summary>
    /// Initiates a VPN connection to the specified server using the given protocol.
    /// </summary>
    Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects the active VPN session.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current connection status.
    /// </summary>
    ConnectionStatus Status { get; }

    /// <summary>
    /// Gets live connection statistics (speed, data, duration).
    /// </summary>
    ConnectionStats? CurrentStats { get; }

    /// <summary>
    /// Gets the VPN-assigned IP address when connected.
    /// </summary>
    string? VpnIpAddress { get; }

    /// <summary>
    /// Gets the last error message from a failed connection attempt.
    /// Null when no error has occurred since the last successful connection.
    /// </summary>
    string? LastErrorMessage { get; }

    /// <summary>
    /// Raised when connection status changes.
    /// </summary>
    event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>
    /// Raised when a connection error occurs with an actionable message.
    /// </summary>
    event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Raised when connection statistics are updated.
    /// </summary>
    event EventHandler<ConnectionStats>? StatsUpdated;
}
