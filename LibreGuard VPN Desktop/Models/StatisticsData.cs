namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Daily data usage record for statistics view.
/// </summary>
public record DailyDataUsage(string Day, double DownloadMb, double UploadMb);

/// <summary>
/// Daily connection duration record for statistics view.
/// </summary>
public record ConnectionHistoryEntry(string Date, double DurationHours);

/// <summary>
/// Server usage breakdown for pie chart display.
/// </summary>
public record ServerUsageEntry(string ServerName, double Percentage);

/// <summary>
/// Aggregated statistics for a selected period.
/// </summary>
public record StatisticsSummary(
    int TotalConnections,
    double TotalDownloadMb,
    double TotalUploadMb,
    double TotalDurationHours);

/// <summary>
/// Persistent record of a single VPN connection session.
/// </summary>
public record VpnSessionRecord(
    DateTime StartTime,
    DateTime EndTime,
    string ServerName,
    double DownloadMb,
    double UploadMb);
