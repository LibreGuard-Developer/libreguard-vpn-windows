using System.Globalization;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Server service backed by the LibreGuard management API.
/// Fetches the server list from GET /api/vpn/servers and manages local favorites/recent.
/// </summary>
internal sealed class ApiServerService : IServerService
{
    private static readonly Dictionary<string, string> CountryFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["United States"] = "\U0001F1FA\U0001F1F8",
        ["Canada"] = "\U0001F1E8\U0001F1E6",
        ["United Kingdom"] = "\U0001F1EC\U0001F1E7",
        ["Germany"] = "\U0001F1E9\U0001F1EA",
        ["France"] = "\U0001F1EB\U0001F1F7",
        ["Netherlands"] = "\U0001F1F3\U0001F1F1",
        ["Japan"] = "\U0001F1EF\U0001F1F5",
        ["Singapore"] = "\U0001F1F8\U0001F1EC",
        ["Australia"] = "\U0001F1E6\U0001F1FA",
        ["Brazil"] = "\U0001F1E7\U0001F1F7",
        ["India"] = "\U0001F1EE\U0001F1F3",
        ["South Korea"] = "\U0001F1F0\U0001F1F7",
        ["Sweden"] = "\U0001F1F8\U0001F1EA",
        ["Switzerland"] = "\U0001F1E8\U0001F1ED",
        ["Italy"] = "\U0001F1EE\U0001F1F9",
        ["Spain"] = "\U0001F1EA\U0001F1F8",
        ["Poland"] = "\U0001F1F5\U0001F1F1",
        ["Romania"] = "\U0001F1F7\U0001F1F4",
        ["Serbia"] = "\U0001F1F7\U0001F1F8",
        ["Croatia"] = "\U0001F1ED\U0001F1F7",
        ["Slovenia"] = "\U0001F1F8\U0001F1EE",
        ["Bosnia and Herzegovina"] = "\U0001F1E7\U0001F1E6",
        ["Montenegro"] = "\U0001F1F2\U0001F1EA",
        ["Norway"] = "\U0001F1F3\U0001F1F4",
        ["Denmark"] = "\U0001F1E9\U0001F1F0",
        ["Finland"] = "\U0001F1EB\U0001F1EE",
        ["Belgium"] = "\U0001F1E7\U0001F1EA",
        ["Austria"] = "\U0001F1E6\U0001F1F9",
        ["Ireland"] = "\U0001F1EE\U0001F1EA",
        ["Portugal"] = "\U0001F1F5\U0001F1F9",
        ["Greece"] = "\U0001F1EC\U0001F1F7",
        ["Turkey"] = "\U0001F1F9\U0001F1F7",
        ["Mexico"] = "\U0001F1F2\U0001F1FD",
        ["Argentina"] = "\U0001F1E6\U0001F1F7",
        ["Chile"] = "\U0001F1E8\U0001F1F1",
        ["South Africa"] = "\U0001F1FF\U0001F1E6",
        ["Israel"] = "\U0001F1EE\U0001F1F1",
        ["United Arab Emirates"] = "\U0001F1E6\U0001F1EA",
        ["Hong Kong"] = "\U0001F1ED\U0001F1F0",
        ["Taiwan"] = "\U0001F1F9\U0001F1FC",
        ["New Zealand"] = "\U0001F1F3\U0001F1FF",
    };

    private readonly ApiHttpClientService _api;
    private readonly PingService _pingService;
    private readonly List<string> _favorites = [];
    private readonly List<string> _recent = [];

    public ApiServerService(ApiHttpClientService api, PingService pingService)
    {
        _api = api;
        _pingService = pingService;
    }

    public async Task<IReadOnlyList<ServerLocation>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        var response = await _api.GetAsync<VpnServerListResponse>("api/vpn/servers", cancellationToken);
        if (response?.Servers is null or { Count: 0 })
            return [];

        var servers = response.Servers
            .Select(MapToServerLocation)
            .ToList();

        // Measure ping in background without blocking the server list load
        _ = MeasurePingsAsync(servers, cancellationToken);

        return servers.AsReadOnly();
    }

    /// <summary>
    /// Measures ping latency for all servers and updates their PingMs values.
    /// Runs concurrently with a 10-second total timeout.
    /// Uses ServerHostname and LatencyPingPort from each server.
    /// </summary>
    private async Task MeasurePingsAsync(List<ServerLocation> servers, CancellationToken cancellationToken = default)
    {
        try
        {
            // Filter servers with hostnames and create (server, hostname, port) tuples
            var serversToPing = servers
                .Where(s => !string.IsNullOrEmpty(s.ServerHostname))
                .ToList();

            if (serversToPing.Count == 0)
                return; // No servers to ping

            // Create a cancellation token with overall timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // Overall timeout for all pings

            // Launch ping tasks for all servers
            var pingTasks = serversToPing
                .Select(async server =>
                {
                    try
                    {
                        var latency = await _pingService.MeasureLatencyAsync(
                            server.ServerHostname!,
                            server.LatencyPingPort,
                            cts.Token);

                        return (server, latency);
                    }
                    catch
                    {
                        return (server, latency: -1);
                    }
                })
                .ToList();

            try
            {
                await Task.WhenAll(pingTasks);
            }
            catch (OperationCanceledException)
            {
                // Timeout occurred - use partial results
            }

            // Update server ping values with results
            foreach (var task in pingTasks)
            {
                try
                {
                    var (server, latency) = await task;
                    if (latency >= 0)
                    {
                        server.PingMs = latency;
                    }
                    // If latency is -1 (failed), server keeps 0ms default
                }
                catch
                {
                    // Skip any failed tasks
                }
            }
        }
        catch
        {
            // Silently fail - ping measurement is best-effort and shouldn't break server loading
        }
    }

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

    private static ServerLocation MapToServerLocation(VpnServerDto dto)
    {
        var flag = GetFlag(dto.Country);
        var flagUrl = GetFlagUrl(dto.Country);
        var isPremium = string.Equals(dto.PricingTier, "Premium", StringComparison.OrdinalIgnoreCase);
        var loadPercent = dto.Load.HasValue ? (int)Math.Round(dto.Load.Value) : 0;

        return new ServerLocation(
            id: dto.Id.ToString(CultureInfo.InvariantCulture),
            country: dto.Country,
            city: dto.City ?? string.Empty,
            serverName: dto.ServerName,
            flag: flag,
            flagUrl: flagUrl,
            pingMs: 0, // Ping is measured client-side via latencyPingPort
            loadPercent: loadPercent,
            isPremium: isPremium,
            serverIp: dto.ServerIp,
            serverHostname: dto.ServerHostname,
            linkSpeedMbps: dto.LinkSpeed,
            latencyPingPort: dto.LatencyPingPort);
    }

    private static string GetFlag(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return "\U0001F310"; // Globe

        if (CountryFlags.TryGetValue(country, out var flag))
            return flag;

        // Try matching by 2-letter ISO code if provided
        if (country.Length == 2)
        {
            try
            {
                var first = char.ToUpperInvariant(country[0]) - 'A' + 0x1F1E6;
                var second = char.ToUpperInvariant(country[1]) - 'A' + 0x1F1E6;
                return char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second);
            }
            catch { }
        }

        // Common name mapping fallbacks
        var normalized = country.ToLowerInvariant();
        if (normalized.Contains("united states") || normalized == "us" || normalized == "usa") return "\U0001F1FA\U0001F1F8";
        if (normalized.Contains("united kingdom") || normalized == "uk" || normalized == "gb") return "\U0001F1EC\U0001F1E7";
        if (normalized.Contains("canad")) return "\U0001F1E8\U0001F1E6";
        if (normalized.Contains("german")) return "\U0001F1E9\U0001F1EA";
        if (normalized.Contains("franc")) return "\U0001F1EB\U0001F1F7";
        if (normalized.Contains("japan")) return "\U0001F1EF\U0001F1F5";
        if (normalized == "uae") return "\U0001F1E6\U0001F1EA";

        return "\U0001F310"; // Fallback to globe
    }

    private static readonly Dictionary<string, string> CountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["United States"] = "us",
        ["Canada"] = "ca",
        ["United Kingdom"] = "gb",
        ["Germany"] = "de",
        ["France"] = "fr",
        ["Netherlands"] = "nl",
        ["Japan"] = "jp",
        ["Singapore"] = "sg",
        ["Australia"] = "au",
        ["Brazil"] = "br",
        ["India"] = "in",
        ["South Korea"] = "kr",
        ["Sweden"] = "se",
        ["Switzerland"] = "ch",
        ["Italy"] = "it",
        ["Spain"] = "es",
        ["Poland"] = "pl",
        ["Romania"] = "ro",
        ["Serbia"] = "rs",
        ["Croatia"] = "hr",
        ["Slovenia"] = "si",
        ["Bosnia and Herzegovina"] = "ba",
        ["Montenegro"] = "me",
        ["Norway"] = "no",
        ["Denmark"] = "dk",
        ["Finland"] = "fi",
        ["Belgium"] = "be",
        ["Austria"] = "at",
        ["Ireland"] = "ie",
        ["Portugal"] = "pt",
        ["Greece"] = "gr",
        ["Turkey"] = "tr",
        ["Mexico"] = "mx",
        ["Argentina"] = "ar",
        ["Chile"] = "cl",
        ["South Africa"] = "za",
        ["Israel"] = "il",
        ["United Arab Emirates"] = "ae",
        ["Hong Kong"] = "hk",
        ["Taiwan"] = "tw",
        ["New Zealand"] = "nz",
    };

    private static string? GetFlagUrl(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return null;

        string? code = null;
        if (CountryCodes.TryGetValue(country, out var mappedCode))
        {
            code = mappedCode;
        }
        else if (country.Length == 2)
        {
            code = country.ToLowerInvariant();
        }
        else
        {
            // Try lenient matching
            var normalized = country.ToLowerInvariant();
            if (normalized.Contains("united states") || normalized == "usa") code = "us";
            else if (normalized.Contains("united kingdom") || normalized == "uk") code = "gb";
            else if (normalized == "uae") code = "ae";
        }

        return code is not null ? $"https://flagcdn.com/w40/{code}.png" : null;
    }
}
