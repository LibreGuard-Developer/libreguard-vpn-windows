using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Provides a hardcoded server list and in-memory favorites/recent tracking for UI development.
/// </summary>
internal sealed class MockServerService : IServerService
{
    private readonly List<string> _favorites = [];
    private readonly List<string> _recent = [];

    private static readonly IReadOnlyList<ServerLocation> s_servers =
    [
        new("1",  "United States", "New York",    "US-MULTI-1",  "\U0001F1FA\U0001F1F8", "https://flagcdn.com/w40/us.png", 12,  45),
        new("2",  "United States", "Los Angeles", "US-MULTI-20", "\U0001F1FA\U0001F1F8", "https://flagcdn.com/w40/us.png", 28,  62),
        new("8",  "Canada",        "Toronto",     "CA-MULTI-2",  "\U0001F1E8\U0001F1E6", "https://flagcdn.com/w40/ca.png", 22,  33),
        new("3",  "United Kingdom","London",      "UK-MULTI-5",  "\U0001F1EC\U0001F1E7", "https://flagcdn.com/w40/gb.png", 45,  38),
        new("4",  "Germany",       "Frankfurt",   "DE-MULTI-1",  "\U0001F1E9\U0001F1EA", "https://flagcdn.com/w40/de.png", 52,  51),
        new("9",  "France",        "Paris",       "FR-MULTI-9",  "\U0001F1EB\U0001F1F7", "https://flagcdn.com/w40/fr.png", 48,  55),
        new("10", "Netherlands",   "Amsterdam",   "NL-MULTI-4",  "\U0001F1F3\U0001F1F1", "https://flagcdn.com/w40/nl.png", 41,  48),
        new("5",  "Japan",         "Tokyo",       "JP-MULTI-3",  "\U0001F1EF\U0001F1F5", "https://flagcdn.com/w40/jp.png", 98,  29),
        new("6",  "Singapore",     "Singapore",   "SG-MULTI-12", "\U0001F1F8\U0001F1EC", "https://flagcdn.com/w40/sg.png", 112, 67),
        new("7",  "Australia",     "Sydney",      "AU-MULTI-7",  "\U0001F1E6\U0001F1FA", "https://flagcdn.com/w40/au.png", 145, 42),
        new("11", "Brazil",        "S\u00E3o Paulo",   "BR-MULTI-8",  "\U0001F1E7\U0001F1F7", "https://flagcdn.com/w40/br.png", 156, 71, isPremium: true),
        new("12", "India",         "Mumbai",      "IN-MULTI-15", "\U0001F1EE\U0001F1F3", "https://flagcdn.com/w40/in.png", 132, 68, isPremium: true),
        new("13", "South Korea",   "Seoul",       "KR-MULTI-6",  "\U0001F1F0\U0001F1F7", "https://flagcdn.com/w40/kr.png", 102, 44, isPremium: true),
    ];

    public Task<IReadOnlyList<ServerLocation>> GetServersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(s_servers);

    public IReadOnlyList<string> GetFavorites() => _favorites.AsReadOnly();

    public void ToggleFavorite(string serverId)
    {
        if (!_favorites.Remove(serverId))
            _favorites.Add(serverId);
    }

    public IReadOnlyList<string> GetRecent() => _recent.AsReadOnly();

    public void AddRecent(string serverId)
    {
        _recent.Remove(serverId);
        _recent.Insert(0, serverId);
        if (_recent.Count > 5)
            _recent.RemoveAt(_recent.Count - 1);
    }
}
