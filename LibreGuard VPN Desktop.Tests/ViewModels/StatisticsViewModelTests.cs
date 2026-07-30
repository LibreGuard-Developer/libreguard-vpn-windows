using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Tests.ViewModels;

public sealed class StatisticsViewModelTests
{
    [Fact]
    public async Task StatsUpdated_WhenConnected_IncludesActiveSessionInTotalsAndConnectionCount()
    {
        var statistics = new StubStatisticsService();
        var vpn = new ControllableVpnConnectionService();
        var viewModel = new StatisticsViewModel(statistics, vpn);
        await viewModel.RefreshAsync();

        vpn.SimulateStatusChange(ConnectionStatus.Connected);
        vpn.SimulateStatsUpdate(new ConnectionStats(
            DownloadSpeedMbps: 0,
            UploadSpeedMbps: 0,
            SessionDataMb: 300,
            Duration: TimeSpan.FromHours(2),
            SessionDownloadMb: 200,
            SessionUploadMb: 100));

        Assert.Equal(1, viewModel.TotalConnections);
        Assert.Equal(200.0 / 1024, viewModel.TotalDownloadGb, precision: 6);
        Assert.Equal(100.0 / 1024, viewModel.TotalUploadGb, precision: 6);
        Assert.Equal(2, viewModel.AvgSessionHours, precision: 6);
        Assert.Contains(viewModel.DataUsageByDay, d => d.DownloadMb == 200 && d.UploadMb == 100);
        Assert.Contains(viewModel.ConnectionHistory, d => d.DurationHours == 2);
    }

    [Fact]
    public async Task StatusChanged_WhenDisconnected_RefreshesPersistedSummary()
    {
        var statistics = new StubStatisticsService();
        var vpn = new ControllableVpnConnectionService();
        var viewModel = new StatisticsViewModel(statistics, vpn);
        await viewModel.RefreshAsync();

        vpn.SimulateStatusChange(ConnectionStatus.Connected);
        vpn.SimulateStatsUpdate(new ConnectionStats(0, 0, 150, TimeSpan.FromHours(1), 120, 30));
        Assert.Equal(1, viewModel.TotalConnections);

        statistics.Summary = new StatisticsSummary(1, 120, 30, 1);
        statistics.DailyUsage = [CreateTodayUsage(120, 30)];
        statistics.ConnectionHistory = [CreateTodayDuration(1)];

        vpn.SimulateStatusChange(ConnectionStatus.Disconnected);
        await Task.Delay(50);

        Assert.Equal(1, viewModel.TotalConnections);
        Assert.Equal(120.0 / 1024, viewModel.TotalDownloadGb, precision: 6);
        Assert.Equal(30.0 / 1024, viewModel.TotalUploadGb, precision: 6);
        Assert.Equal(1, viewModel.AvgSessionHours, precision: 6);
    }

    [Fact]
    public async Task PeriodChanged_RecomputesBaseDataAndKeepsLiveOverlay()
    {
        var statistics = new StubStatisticsService
        {
            Summary = new StatisticsSummary(2, 500, 100, 4),
            DailyUsage = [CreateTodayUsage(500, 100)],
            ConnectionHistory = [CreateTodayDuration(4)]
        };
        var vpn = new ControllableVpnConnectionService();
        var viewModel = new StatisticsViewModel(statistics, vpn);
        await viewModel.RefreshAsync();

        vpn.SimulateStatusChange(ConnectionStatus.Connected);
        vpn.SimulateStatsUpdate(new ConnectionStats(0, 0, 60, TimeSpan.FromHours(1), 40, 20));

        viewModel.Period = "month";
        await Task.Delay(50);

        Assert.Equal("month", statistics.LastSummaryPeriod);
        Assert.Equal(3, viewModel.TotalConnections);
        Assert.Equal(540.0 / 1024, viewModel.TotalDownloadGb, precision: 6);
        Assert.Equal(120.0 / 1024, viewModel.TotalUploadGb, precision: 6);
        Assert.Equal(5.0 / 3.0, viewModel.AvgSessionHours, precision: 6);
    }

    private static DailyDataUsage CreateTodayUsage(double downloadMb, double uploadMb)
    {
        return new DailyDataUsage(DateTime.UtcNow.ToString("ddd"), downloadMb, uploadMb);
    }

    private static ConnectionHistoryEntry CreateTodayDuration(double durationHours)
    {
        return new ConnectionHistoryEntry(DateTime.UtcNow.ToString("MM/dd"), durationHours);
    }

    private sealed class StubStatisticsService : IStatisticsService
    {
        public StatisticsSummary Summary { get; set; } = new(0, 0, 0, 0);
        public IEnumerable<DailyDataUsage> DailyUsage { get; set; } = [CreateTodayUsage(0, 0)];
        public IEnumerable<ConnectionHistoryEntry> ConnectionHistory { get; set; } = [CreateTodayDuration(0)];
        public IEnumerable<ServerUsageEntry> ServerUsage { get; set; } = [];
        public string? LastSummaryPeriod { get; private set; }

        public Task RecordSessionAsync(VpnSessionRecord record) => Task.CompletedTask;

        public Task<StatisticsSummary> GetSummaryAsync(string period)
        {
            LastSummaryPeriod = period;
            return Task.FromResult(Summary);
        }

        public Task<IEnumerable<DailyDataUsage>> GetDailyUsageAsync(string period) => Task.FromResult(DailyUsage);
        public Task<IEnumerable<ConnectionHistoryEntry>> GetConnectionHistoryAsync(string period) => Task.FromResult(ConnectionHistory);
        public Task<IEnumerable<ServerUsageEntry>> GetServerUsageAsync(string period) => Task.FromResult(ServerUsage);
    }

    private sealed class ControllableVpnConnectionService : IVpnConnectionService
    {
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
        public ConnectionStats? CurrentStats { get; private set; }
        public string? VpnIpAddress => null;
        public string? LastErrorMessage => null;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<ConnectionStats>? StatsUpdated;

        public void SimulateStatusChange(ConnectionStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }

        public void SimulateStatsUpdate(ConnectionStats stats)
        {
            CurrentStats = stats;
            StatsUpdated?.Invoke(this, stats);
        }

        public Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default)
        {
            SimulateStatusChange(ConnectionStatus.Connected);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            SimulateStatusChange(ConnectionStatus.Disconnected);
            return Task.CompletedTask;
        }

        public void RaiseErrorForCompiler()
        {
            ErrorOccurred?.Invoke(this, string.Empty);
        }
    }
}
