using System.IO;
using System.Text.Json;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Persists VPN statistics to a local JSON file in the application data folder.
/// </summary>
public sealed class LocalStatisticsService : IStatisticsService
{
    private static readonly string StatsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibreGuardVPN");

    private readonly TokenStorageService _tokenStorage;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private List<VpnSessionRecord>? _cache;

    public LocalStatisticsService(TokenStorageService tokenStorage)
    {
        _tokenStorage = tokenStorage;
        _tokenStorage.SessionChanged += () =>
        {
            _fileLock.Wait();
            try { _cache = null; } finally { _fileLock.Release(); }
        };
    }

    private string GetStatsFilePath()
    {
        var id = !string.IsNullOrEmpty(_tokenStorage.UserId) ? _tokenStorage.UserId : "default";
        return Path.Combine(StatsDirectory, $"stats_{id}.json");
    }

    public async Task RecordSessionAsync(VpnSessionRecord record)
    {
        await _fileLock.WaitAsync();
        try
        {
            var history = await LoadHistoryInternalAsync();
            history.Add(record);
            await SaveHistoryInternalAsync(history);
            _cache = history;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<IEnumerable<DailyDataUsage>> GetDailyUsageAsync(string period)
    {
        var history = await LoadHistoryInternalAsync();
        var cutoff = GetCutoff(period);
        var filteredHistory = history.Where(r => r.StartTime >= cutoff).ToList();
        
        var now = DateTime.UtcNow;
        var end = now.Date;

        if (period == "year")
        {
            // Group by month for year view
            var months = Enumerable.Range(0, 12)
                .Select(i => end.AddMonths(-i))
                .OrderBy(m => m)
                .Select(m => new DateTime(m.Year, m.Month, 1))
                .ToList();

            var monthlyData = filteredHistory
                .GroupBy(r => new DateTime(r.StartTime.Year, r.StartTime.Month, 1))
                .ToDictionary(g => g.Key, g => (DownloadMb: g.Sum(r => r.DownloadMb), UploadMb: g.Sum(r => r.UploadMb)));

            return months.Select(m => new DailyDataUsage(
                Day: m.ToString("MMM"),
                DownloadMb: monthlyData.TryGetValue(m, out var data) ? data.DownloadMb : 0,
                UploadMb: monthlyData.TryGetValue(m, out data) ? data.UploadMb : 0)).ToList();
        }

        // Default grouping by day (for week/month/day)
        var days = GetDateBuckets(period, filteredHistory, end);

        var dailyData = filteredHistory
            .GroupBy(r => r.StartTime.Date)
            .ToDictionary(g => g.Key, g => (DownloadMb: g.Sum(r => r.DownloadMb), UploadMb: g.Sum(r => r.UploadMb)));

        return days.Select(d => new DailyDataUsage(
            Day: GetDayLabel(d, period),
            DownloadMb: dailyData.TryGetValue(d, out var data) ? data.DownloadMb : 0,
            UploadMb: dailyData.TryGetValue(d, out data) ? data.UploadMb : 0)).ToList();
    }

    public async Task<StatisticsSummary> GetSummaryAsync(string period)
    {
        var history = await GetHistoryForPeriodAsync(period);

        return new StatisticsSummary(
            TotalConnections: history.Count,
            TotalDownloadMb: history.Sum(r => r.DownloadMb),
            TotalUploadMb: history.Sum(r => r.UploadMb),
            TotalDurationHours: history.Sum(r => Math.Max(0, (r.EndTime - r.StartTime).TotalHours)));
    }

    public async Task<IEnumerable<ConnectionHistoryEntry>> GetConnectionHistoryAsync(string period)
    {
        var history = await LoadHistoryInternalAsync();
        var cutoff = GetCutoff(period);
        var filteredHistory = history.Where(r => r.StartTime >= cutoff).ToList();

        var now = DateTime.UtcNow;
        var end = now.Date;

        if (period == "year")
        {
            var months = Enumerable.Range(0, 12)
                .Select(i => end.AddMonths(-i))
                .OrderBy(m => m)
                .Select(m => new DateTime(m.Year, m.Month, 1))
                .ToList();

            var monthlyData = filteredHistory
                .GroupBy(r => new DateTime(r.StartTime.Year, r.StartTime.Month, 1))
                .ToDictionary(g => g.Key, g => g.Sum(r => (r.EndTime - r.StartTime).TotalHours));

            return months.Select(m => new ConnectionHistoryEntry(
                Date: m.ToString("MMM"),
                DurationHours: monthlyData.TryGetValue(m, out var duration) ? duration : 0)).ToList();
        }

        var days = GetDateBuckets(period, filteredHistory, end);

        var dailyData = filteredHistory
            .GroupBy(r => r.StartTime.Date)
            .ToDictionary(g => g.Key, g => g.Sum(r => (r.EndTime - r.StartTime).TotalHours));

        return days.Select(d => new ConnectionHistoryEntry(
            Date: d.ToString("MM/dd"),
            DurationHours: dailyData.TryGetValue(d, out var duration) ? duration : 0)).ToList();
    }

    public async Task<IEnumerable<ServerUsageEntry>> GetServerUsageAsync(string period)
    {
        var history = await GetHistoryForPeriodAsync(period);
        if (!history.Any()) return [];

        var totalSessions = history.Count;
        return history
            .GroupBy(r => r.ServerName)
            .Select(g => new ServerUsageEntry(
                ServerName: g.Key,
                Percentage: (double)g.Count() / totalSessions * 100))
            .OrderByDescending(s => s.Percentage)
            .Take(5)
            .ToList();
    }

    private async Task<List<VpnSessionRecord>> GetHistoryForPeriodAsync(string period)
    {
        var history = await LoadHistoryInternalAsync();
        var cutoff = GetCutoff(period);

        // Filter by cutoff and ensure we handle local time if recorded in UTC
        return history.Where(r => r.StartTime >= cutoff).ToList();
    }

    private static List<DateTime> GetDateBuckets(string period, List<VpnSessionRecord> filteredHistory, DateTime end)
    {
        var dayCount = period switch
        {
            "day" => 1,
            "week" => 7,
            "month" => 30,
            _ => 0
        };

        if (dayCount > 0)
        {
            var start = end.AddDays(-(dayCount - 1));
            return Enumerable.Range(0, dayCount)
                .Select(i => start.AddDays(i))
                .ToList();
        }

        var fallbackStart = filteredHistory.Any()
            ? filteredHistory.Min(r => r.StartTime).Date
            : end;

        return Enumerable.Range(0, (end - fallbackStart).Days + 1)
            .Select(i => fallbackStart.AddDays(i))
            .ToList();
    }

    private static DateTime GetCutoff(string period)
    {
        return period switch
        {
            "day" => DateTime.UtcNow.AddDays(-1),
            "week" => DateTime.UtcNow.AddDays(-7),
            "month" => DateTime.UtcNow.AddDays(-30),
            "year" => DateTime.UtcNow.AddDays(-365),
            _ => DateTime.MinValue
        };
    }

    private string GetDayLabel(DateTime date, string period)
    {
        return period switch
        {
            "week" => date.ToString("ddd"), // Mon, Tue...
            "month" => date.ToString("dd"), // 01, 02...
            _ => date.ToString("MMM")       // Jan, Feb...
        };
    }

    private async Task<List<VpnSessionRecord>> LoadHistoryInternalAsync()
    {
        if (_cache != null) return _cache;

        var path = GetStatsFilePath();
        if (!File.Exists(path))
            return [];

        try
        {
            using var stream = File.OpenRead(path);
            _cache = await JsonSerializer.DeserializeAsync<List<VpnSessionRecord>>(stream) ?? [];
            return _cache;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveHistoryInternalAsync(List<VpnSessionRecord> history)
    {
        if (!Directory.Exists(StatsDirectory))
            Directory.CreateDirectory(StatsDirectory);

        var path = GetStatsFilePath();
        using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, history, new JsonSerializerOptions { WriteIndented = true });
    }
}
