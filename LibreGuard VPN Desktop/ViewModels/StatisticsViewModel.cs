using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Statistics screen that visualizes local VPN usage data.
/// </summary>
public sealed partial class StatisticsViewModel : ObservableObject
{
    private readonly IStatisticsService _statisticsService;
    private readonly IVpnConnectionService _vpnService;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private List<DailyDataUsage> _baseDataUsageByDay = [];
    private List<ConnectionHistoryEntry> _baseConnectionHistory = [];
    private List<ServerUsageEntry> _baseServerUsage = [];
    private StatisticsSummary _baseSummary = new(0, 0, 0, 0);
    private ConnectionStats? _activeStats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodLabel))]
    private string _period = "week";

    public ObservableCollection<DailyDataUsage> DataUsageByDay { get; } = [];
    public ObservableCollection<ConnectionHistoryEntry> ConnectionHistory { get; } = [];
    public ObservableCollection<ServerUsageEntry> ServerUsage { get; } = [];

    public StatisticsViewModel(IStatisticsService statisticsService, IVpnConnectionService vpnService)
    {
        _statisticsService = statisticsService;
        _vpnService = vpnService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _refreshTimer.Tick += async (s, e) => await RefreshAsync();
        _refreshTimer.Start();

        _vpnService.StatsUpdated += OnVpnStatsUpdated;
        _vpnService.StatusChanged += OnVpnStatusChanged;

        _activeStats = _vpnService.Status == ConnectionStatus.Connected
            ? _vpnService.CurrentStats
            : null;

        _ = RefreshAsync();
    }

    async partial void OnPeriodChanged(string value) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        var summary = await _statisticsService.GetSummaryAsync(Period);
        var dailyUsage = await _statisticsService.GetDailyUsageAsync(Period);
        var connHistory = await _statisticsService.GetConnectionHistoryAsync(Period);
        var serverUsage = await _statisticsService.GetServerUsageAsync(Period);

        await InvokeOnDispatcherAsync(() =>
        {
            _baseSummary = summary;
            _baseDataUsageByDay = dailyUsage.ToList();
            _baseConnectionHistory = connHistory.ToList();
            _baseServerUsage = serverUsage.ToList();
            _activeStats = _vpnService.Status == ConnectionStatus.Connected
                ? _vpnService.CurrentStats
                : null;

            ApplyActiveOverlay();
        });
    }

    private void OnVpnStatsUpdated(object? sender, ConnectionStats stats)
    {
        void Apply()
        {
            _activeStats = stats;
            ApplyActiveOverlay();
        }

        if (_dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.BeginInvoke(Apply);
    }

    private void OnVpnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status == ConnectionStatus.Connected)
        {
            void Apply()
            {
                _activeStats = _vpnService.CurrentStats;
                ApplyActiveOverlay();
            }

            if (_dispatcher.CheckAccess())
                Apply();
            else
                _dispatcher.BeginInvoke(Apply);

            return;
        }

        if (status == ConnectionStatus.Disconnected)
        {
            _activeStats = null;
            _ = RefreshAsync();
        }
    }

    private void ApplyActiveOverlay()
    {
        var dailyUsage = _baseDataUsageByDay.ToList();
        var connectionHistory = _baseConnectionHistory.ToList();
        var activeStats = IsActiveSessionInPeriod() ? _activeStats : null;

        if (activeStats is not null)
        {
            var labelDate = DateTime.UtcNow.Date;
            var usageLabel = Period == "year"
                ? labelDate.ToString("MMM")
                : GetDayLabel(labelDate, Period);
            var durationLabel = Period == "year"
                ? labelDate.ToString("MMM")
                : labelDate.ToString("MM/dd");
            var (downloadMb, uploadMb) = GetActiveSessionTraffic(activeStats);

            AddUsageToBucket(dailyUsage, usageLabel, downloadMb, uploadMb);
            AddDurationToBucket(connectionHistory, durationLabel, activeStats.Duration.TotalHours);
        }

        DataUsageByDay.Clear();
        foreach (var item in dailyUsage) DataUsageByDay.Add(item);

        ConnectionHistory.Clear();
        foreach (var item in connectionHistory) ConnectionHistory.Add(item);

        ServerUsage.Clear();
        foreach (var item in _baseServerUsage) ServerUsage.Add(item);

        OnPropertyChanged(nameof(TotalDataGb));
        OnPropertyChanged(nameof(TotalDownloadGb));
        OnPropertyChanged(nameof(TotalUploadGb));
        OnPropertyChanged(nameof(AvgDailyDownloadMb));
        OnPropertyChanged(nameof(AvgDailyUploadMb));
        OnPropertyChanged(nameof(AvgSessionHours));
        OnPropertyChanged(nameof(TotalConnections));
        OnPropertyChanged(nameof(PeriodLabel));
    }

    public double TotalDataGb => (TotalDownloadMb + TotalUploadMb) / 1024;
    public double TotalDownloadGb => TotalDownloadMb / 1024;
    public double TotalUploadGb => TotalUploadMb / 1024;
    
    public double AvgDailyDownloadMb => DataUsageByDay.Any() 
        ? DataUsageByDay.Average(d => d.DownloadMb) 
        : 0;

    public double AvgDailyUploadMb => DataUsageByDay.Any() 
        ? DataUsageByDay.Average(d => d.UploadMb) 
        : 0;

    public double AvgSessionHours => TotalConnections > 0
        ? TotalDurationHours / TotalConnections
        : 0;

    public int TotalConnections => _baseSummary.TotalConnections + (IsActiveSessionInPeriod() ? 1 : 0);

    public string PeriodLabel => Period switch
    {
        "day" => "Today",
        "week" => "This week",
        "month" => "This month",
        "year" => "This year",
        _ => "Selected period"
    };

    private double TotalDownloadMb => _baseSummary.TotalDownloadMb + ActiveDownloadMb;
    private double TotalUploadMb => _baseSummary.TotalUploadMb + ActiveUploadMb;
    private double TotalDurationHours => _baseSummary.TotalDurationHours + ActiveDurationHours;

    private double ActiveDownloadMb => IsActiveSessionInPeriod() && _activeStats is not null
        ? GetActiveSessionTraffic(_activeStats).DownloadMb
        : 0;

    private double ActiveUploadMb => IsActiveSessionInPeriod() && _activeStats is not null
        ? GetActiveSessionTraffic(_activeStats).UploadMb
        : 0;

    private double ActiveDurationHours => IsActiveSessionInPeriod() && _activeStats is not null
        ? _activeStats.Duration.TotalHours
        : 0;

    private bool IsActiveSessionInPeriod()
    {
        if (_activeStats is null || _vpnService.Status != ConnectionStatus.Connected)
            return false;

        var cutoff = Period switch
        {
            "day" => DateTime.UtcNow.AddDays(-1),
            "week" => DateTime.UtcNow.AddDays(-7),
            "month" => DateTime.UtcNow.AddDays(-30),
            "year" => DateTime.UtcNow.AddDays(-365),
            _ => DateTime.MinValue
        };

        return DateTime.UtcNow >= cutoff;
    }

    private static (double DownloadMb, double UploadMb) GetActiveSessionTraffic(ConnectionStats stats)
    {
        if (stats.SessionDownloadMb > 0 || stats.SessionUploadMb > 0)
            return (stats.SessionDownloadMb, stats.SessionUploadMb);

        var totalSpeed = stats.DownloadSpeedMbps + stats.UploadSpeedMbps;
        if (totalSpeed > 0)
        {
            var downloadRatio = stats.DownloadSpeedMbps / totalSpeed;
            var downloadMb = stats.SessionDataMb * downloadRatio;
            return (downloadMb, stats.SessionDataMb - downloadMb);
        }

        return (stats.SessionDataMb, 0);
    }

    private static void AddUsageToBucket(List<DailyDataUsage> dailyUsage, string label, double downloadMb, double uploadMb)
    {
        var index = dailyUsage.FindLastIndex(d => d.Day == label);
        if (index >= 0)
        {
            var current = dailyUsage[index];
            dailyUsage[index] = current with
            {
                DownloadMb = current.DownloadMb + downloadMb,
                UploadMb = current.UploadMb + uploadMb
            };
        }
    }

    private static void AddDurationToBucket(List<ConnectionHistoryEntry> connectionHistory, string label, double durationHours)
    {
        var index = connectionHistory.FindLastIndex(c => c.Date == label);
        if (index >= 0)
        {
            var current = connectionHistory[index];
            connectionHistory[index] = current with
            {
                DurationHours = current.DurationHours + durationHours
            };
        }
    }

    private static string GetDayLabel(DateTime date, string period)
    {
        return period switch
        {
            "week" => date.ToString("ddd"),
            "month" => date.ToString("dd"),
            _ => date.ToString("MMM")
        };
    }

    private Task InvokeOnDispatcherAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }
}
