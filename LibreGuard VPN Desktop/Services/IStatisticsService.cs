using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Service for persisting and retrieving VPN connection statistics.
/// </summary>
public interface IStatisticsService
{
    /// <summary>
    /// Records a completed VPN session.
    /// </summary>
    Task RecordSessionAsync(VpnSessionRecord record);

    /// <summary>
    /// Gets aggregate statistics for the specified period.
    /// </summary>
    Task<StatisticsSummary> GetSummaryAsync(string period);

    /// <summary>
    /// Gets aggregated daily data usage for the specified period.
    /// </summary>
    Task<IEnumerable<DailyDataUsage>> GetDailyUsageAsync(string period);

    /// <summary>
    /// Gets aggregated connection duration history for the specified period.
    /// </summary>
    Task<IEnumerable<ConnectionHistoryEntry>> GetConnectionHistoryAsync(string period);

    /// <summary>
    /// Gets server usage distribution for the specified period.
    /// </summary>
    Task<IEnumerable<ServerUsageEntry>> GetServerUsageAsync(string period);
}
