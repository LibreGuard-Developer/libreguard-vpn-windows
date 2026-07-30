using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Provides server list data: available locations, favorites, and recent connections.
/// </summary>
public interface IServerService
{
    /// <summary>
    /// Gets all available server locations.
    /// </summary>
    Task<IReadOnlyList<ServerLocation>> GetServersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the IDs of servers the user has favorited.
    /// </summary>
    IReadOnlyList<string> GetFavorites();

    /// <summary>
    /// Toggles the favorite status of a server.
    /// </summary>
    void ToggleFavorite(string serverId);

    /// <summary>
    /// Gets the IDs of recently connected servers.
    /// </summary>
    IReadOnlyList<string> GetRecent();

    /// <summary>
    /// Records a server as recently used.
    /// </summary>
    void AddRecent(string serverId);
}
