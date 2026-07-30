using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Abstracts the protocol-specific logic for establishing and tearing down VPN tunnels.
/// </summary>
internal interface IVpnTunnelStrategy
{
    /// <summary>
    /// The protocol this strategy handles.
    /// </summary>
    VpnProtocol Protocol { get; }

    /// <summary>
    /// Establishes a VPN tunnel using the given config file.
    /// </summary>
    /// <param name="configPath">Path to the configuration file on disk.</param>
    /// <param name="passphrase">Decrypted passphrase/password, if required by the config.</param>
    /// <param name="serverIp">The VPN server IP address.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ConnectAsync(string configPath, string? passphrase, string serverIp, CancellationToken ct = default);

    /// <summary>
    /// Tears down the active VPN tunnel.
    /// </summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true if a tunnel managed by this strategy is currently active.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Bytes received (downloaded) during the current session.
    /// </summary>
    long BytesIn { get; }

    /// <summary>
    /// Bytes sent (uploaded) during the current session.
    /// </summary>
    long BytesOut { get; }

    /// <summary>
    /// The local IP address assigned to the VPN interface.
    /// </summary>
    string? LocalIp { get; }
}
