namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Snapshot of connection speed and data usage.
/// </summary>
public record ConnectionStats(
    double DownloadSpeedMbps,
    double UploadSpeedMbps,
    double SessionDataMb,
    TimeSpan Duration,
    double SessionDownloadMb = 0,
    double SessionUploadMb = 0);
