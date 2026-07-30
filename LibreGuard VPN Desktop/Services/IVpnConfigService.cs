using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Retrieves VPN configuration files and credentials from the management API.
/// </summary>
internal interface IVpnConfigService
{
    /// <summary>
    /// Requests a VPN configuration (IKEv2 or OpenVPN) for the given server and protocol.
    /// Returns the full config response including embedded certificates and passphrase.
    /// </summary>
    Task<VpnConfigResponse?> GetConfigAsync(int serverId, string protocol, CancellationToken ct = default);

    /// <summary>
    /// Downloads a raw .ovpn configuration file for the given server.
    /// Returns the file bytes, or null if the request fails.
    /// </summary>
    Task<byte[]?> DownloadOpenVpnConfigAsync(int serverId, CancellationToken ct = default);
}
