using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class MockVpnConnectionServiceTests
{
    [Fact]
    public async Task DisconnectAsync_WhenConnected_EmitsDisconnectingBeforeDisconnected()
    {
        var service = new MockVpnConnectionService(new NoOpStatisticsService());
        var statuses = new List<ConnectionStatus>();
        service.StatusChanged += (_, status) => statuses.Add(status);

        await service.ConnectAsync(
            new ServerLocation(
                id: "1",
                country: "Testland",
                city: "Test City",
                serverName: "Test Server",
                flag: "",
                flagUrl: null,
                pingMs: 1,
                loadPercent: 10,
                serverIp: "127.0.0.1"),
            VpnProtocol.IKEv2);
        await service.DisconnectAsync();

        var disconnectingIndex = statuses.IndexOf(ConnectionStatus.Disconnecting);
        var disconnectedIndex = statuses.LastIndexOf(ConnectionStatus.Disconnected);

        Assert.True(disconnectingIndex >= 0);
        Assert.True(disconnectedIndex > disconnectingIndex);
    }

    private sealed class NoOpStatisticsService : IStatisticsService
    {
        public Task RecordSessionAsync(VpnSessionRecord record) => Task.CompletedTask;
        public Task<StatisticsSummary> GetSummaryAsync(string period) => Task.FromResult(new StatisticsSummary(0, 0, 0, 0));
        public Task<IEnumerable<DailyDataUsage>> GetDailyUsageAsync(string period) => Task.FromResult<IEnumerable<DailyDataUsage>>([]);
        public Task<IEnumerable<ConnectionHistoryEntry>> GetConnectionHistoryAsync(string period) => Task.FromResult<IEnumerable<ConnectionHistoryEntry>>([]);
        public Task<IEnumerable<ServerUsageEntry>> GetServerUsageAsync(string period) => Task.FromResult<IEnumerable<ServerUsageEntry>>([]);
    }
}
