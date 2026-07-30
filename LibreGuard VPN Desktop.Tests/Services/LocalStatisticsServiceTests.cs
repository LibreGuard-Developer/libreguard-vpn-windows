using System.IO;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class LocalStatisticsServiceTests
{
    [Fact]
    public async Task GetSummaryAsync_WithEmptyHistory_ReturnsZeroConnections()
    {
        var (service, cleanup) = CreateService();
        try
        {
            var summary = await service.GetSummaryAsync("week");

            Assert.Equal(0, summary.TotalConnections);
            Assert.Equal(0, summary.TotalDownloadMb);
            Assert.Equal(0, summary.TotalUploadMb);
            Assert.Equal(0, summary.TotalDurationHours);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task GetSummaryAsync_DoesNotUseWeekChartBucketCountAsConnections()
    {
        var (service, cleanup) = CreateService();
        try
        {
            var history = await service.GetConnectionHistoryAsync("week");
            var summary = await service.GetSummaryAsync("week");

            Assert.Equal(7, history.Count());
            Assert.Equal(0, summary.TotalConnections);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task GetSummaryAsync_ComputesTotalsAndDurationFromSessions()
    {
        var (service, cleanup) = CreateService();
        try
        {
            var start = DateTime.UtcNow.AddHours(-3);
            await service.RecordSessionAsync(new VpnSessionRecord(
                StartTime: start,
                EndTime: start.AddHours(1),
                ServerName: "Server A",
                DownloadMb: 100,
                UploadMb: 25));
            await service.RecordSessionAsync(new VpnSessionRecord(
                StartTime: start.AddHours(1),
                EndTime: start.AddHours(3),
                ServerName: "Server B",
                DownloadMb: 300,
                UploadMb: 75));

            var summary = await service.GetSummaryAsync("week");

            Assert.Equal(2, summary.TotalConnections);
            Assert.Equal(400, summary.TotalDownloadMb);
            Assert.Equal(100, summary.TotalUploadMb);
            Assert.Equal(3, summary.TotalDurationHours, precision: 3);
        }
        finally
        {
            cleanup();
        }
    }

    private static (LocalStatisticsService Service, Action Cleanup) CreateService()
    {
        var userId = $"test-{Guid.NewGuid():N}";
        var sessionPath = Path.Combine(Path.GetTempPath(), $"libreguard-session-{Guid.NewGuid():N}.secure");
        var tokenStorage = new TokenStorageService(sessionPath)
        {
            UserId = userId
        };
        var service = new LocalStatisticsService(tokenStorage);

        var statsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LibreGuardVPN",
            $"stats_{userId}.json");

        return (service, () =>
        {
            if (File.Exists(statsPath))
                File.Delete(statsPath);
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);
        });
    }
}
