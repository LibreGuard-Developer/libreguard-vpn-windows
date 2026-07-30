using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LibreGuard_VPN_Desktop.Messages;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Dashboard: connection status ring, connect/disconnect, live stats, data usage.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, 
                                                 IRecipient<ServerSelectedMessage>,
                                                 IRecipient<SettingsChangedMessage>
{
    private const long DefaultFreePlanLimitBytes = 5L * 1024 * 1024 * 1024;

    private readonly IVpnConnectionService _vpnService;
    private readonly IServerService _serverService;
    private readonly ISubscriptionService? _subscriptionService;
    private readonly IUserSettingsService? _userSettingsService;
    private readonly KillSwitchManager? _killSwitchManager;
    private readonly INotificationService? _notificationService;
    private readonly IAuthenticationService? _authService;
    private readonly IAccountPlanService? _accountPlanService;
    private readonly PingService _pingService;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;

    private ConnectionStatus _previousStatus = ConnectionStatus.Disconnected;
    private bool _dataWarning80Fired;
    private bool _dataLimitFired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusDescription))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsConnectionTransitionActive))]
    [NotifyPropertyChangedFor(nameof(ConnectionProgressText))]
    [NotifyPropertyChangedFor(nameof(BandwidthFooterText))]
    private ConnectionStatus _status = ConnectionStatus.Disconnected;

    [ObservableProperty]
    private string _connectionTime = "00:00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerSelected))]
    private ServerLocation? _selectedServer;

    public bool IsServerSelected => SelectedServer is not null;

    [ObservableProperty]
    private VpnProtocol _selectedProtocol = VpnProtocol.IKEv2;

    [ObservableProperty]
    private double _downloadSpeed;

    [ObservableProperty]
    private double _uploadSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionDataText))]
    [NotifyPropertyChangedFor(nameof(MonthlyUsagePercent))]
    [NotifyPropertyChangedFor(nameof(IsRunningLow))]
    [NotifyPropertyChangedFor(nameof(CurrentSessionUsagePercent))]
    [NotifyPropertyChangedFor(nameof(PreviousMonthlyUsagePercent))]
    [NotifyPropertyChangedFor(nameof(RemainingUsagePercent))]
    private double _sessionDataMb;

    [ObservableProperty]
    private string? _vpnIpAddress;

    [ObservableProperty]
    private string? _lastErrorMessage;

    [ObservableProperty]
    private string _userIpAddress = "---";

    [ObservableProperty]
    private string _connectedCity = "---";

    [ObservableProperty]
    private string _connectedCountry = "---";

    [ObservableProperty]
    private string _connectedFlag = "";

    [ObservableProperty]
    private string _connectedFlagUrl = "";

    [ObservableProperty]
    private string _connectedServerName = "---";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDataLimited))]
    [NotifyPropertyChangedFor(nameof(IsRunningLow))]
    [NotifyPropertyChangedFor(nameof(MonthlyDataLimitText))]
    [NotifyPropertyChangedFor(nameof(MonthlyDataRemainingText))]
    private UserPlan _plan = UserPlan.Free;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthlyUsagePercent))]
    [NotifyPropertyChangedFor(nameof(IsRunningLow))]
    [NotifyPropertyChangedFor(nameof(MonthlyDataRemainingGb))]
    [NotifyPropertyChangedFor(nameof(MonthlyDataRemainingText))]
    [NotifyPropertyChangedFor(nameof(MonthlyDataUsedText))]
    [NotifyPropertyChangedFor(nameof(CurrentSessionUsagePercent))]
    [NotifyPropertyChangedFor(nameof(PreviousMonthlyUsagePercent))]
    [NotifyPropertyChangedFor(nameof(RemainingUsagePercent))]
    private double _monthlyDataUsedMb = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthlyUsagePercent))]
    [NotifyPropertyChangedFor(nameof(IsRunningLow))]
    [NotifyPropertyChangedFor(nameof(MonthlyDataRemainingGb))]
    [NotifyPropertyChangedFor(nameof(MonthlyDataRemainingText))]
    [NotifyPropertyChangedFor(nameof(MonthlyDataLimitText))]
    [NotifyPropertyChangedFor(nameof(IsDataLimited))]
    [NotifyPropertyChangedFor(nameof(CurrentSessionUsagePercent))]
    [NotifyPropertyChangedFor(nameof(PreviousMonthlyUsagePercent))]
    [NotifyPropertyChangedFor(nameof(RemainingUsagePercent))]
    private double _monthlyDataLimitMb = -1;

    public bool IsDataLimited => Plan == UserPlan.Free && MonthlyDataLimitMb < 1000000;

    public bool IsRunningLow => IsDataLimited && MonthlyUsagePercent >= 80;

    public string MonthlyDataLimitText => Plan == UserPlan.Pro || MonthlyDataLimitMb >= 1000000
        ? "\u221e MB" 
        : MonthlyDataLimitMb < 0 
            ? "---" 
            : MonthlyDataLimitMb >= 1000 
                ? $"{MonthlyDataLimitMb / 1024.0:F1} GB"
                : $"{MonthlyDataLimitMb:F0} MB";

    public string MonthlyDataUsedText => MonthlyDataUsedMb >= 1000 
        ? $"{MonthlyDataUsedMb / 1024.0:F2} GB" 
        : $"{MonthlyDataUsedMb:F0} MB";

    public string SessionDataText => SessionDataMb >= 1000
        ? $"{SessionDataMb / 1024.0:F2} GB"
        : $"{SessionDataMb:F1} MB";

    public string MonthlyDataRemainingText => Plan == UserPlan.Pro || MonthlyDataLimitMb >= 1000000
        ? "Unlimited" 
        : MonthlyDataLimitMb < 0 
            ? "--- GB left" 
            : $"{MonthlyDataRemainingGb:F2} GB left";

    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsConnectionTransitionActive => Status is ConnectionStatus.Connecting
        or ConnectionStatus.Reconnecting
        or ConnectionStatus.Disconnecting;

    public string ConnectionProgressText => Status switch
    {
        ConnectionStatus.Connected => "Tunnel established",
        ConnectionStatus.Connecting => "Securing tunnel",
        ConnectionStatus.Reconnecting => "Restoring tunnel",
        ConnectionStatus.Disconnecting => "Closing tunnel",
        _ => string.Empty
    };

    public string NextQuotaResetText
    {
        get
        {
            var now = DateTime.Now;
            var nextReset = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            return $"Next reset, {nextReset:MMM dd, yyyy}";
        }
    }

    public string BandwidthFooterText => IsConnected
        ? MonthlyDataRemainingText
        : NextQuotaResetText;

    private Task? _refreshDataTask;
    private readonly object _refreshDataLock = new();


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDescription))]
    private bool _isMeasuringLatencies;

    public double MonthlyUsagePercent => MonthlyDataLimitMb > 0
        ? Math.Clamp((MonthlyDataUsedMb + SessionDataMb) / MonthlyDataLimitMb * 100, 0, 100)
        : 0;


    public double CurrentSessionUsagePercent => (IsConnected && MonthlyDataLimitMb > 0 && IsDataLimited)
        ? Math.Clamp(SessionDataMb / MonthlyDataLimitMb * 100, 0, 100)
        : 0;


    public double PreviousMonthlyUsagePercent => IsConnected 
        ? Math.Max(0, MonthlyUsagePercent - CurrentSessionUsagePercent) 
        : MonthlyUsagePercent;

    public double RemainingUsagePercent => Math.Max(0, 100 - PreviousMonthlyUsagePercent - CurrentSessionUsagePercent);

    public double MonthlyDataRemainingGb => MonthlyDataLimitMb > 0 && MonthlyDataLimitMb < 1000000
        ? Math.Max(0, (MonthlyDataLimitMb - MonthlyDataUsedMb) / 1024.0)
        : 0;

    public string StatusText => Status switch
    {
        ConnectionStatus.Connected => "Protected",
        ConnectionStatus.Connecting => "Connecting",
        ConnectionStatus.Disconnecting => "Disconnecting",
        ConnectionStatus.Reconnecting => "Reconnecting\u2026",
        ConnectionStatus.Error => "Connection Error",
        _ => "Not Protected"
    };

    public string StatusDescription
    {
        get
        {
            if (IsMeasuringLatencies)
                return "Measuring server latencies, please wait...";

            return Status switch
            {
                ConnectionStatus.Connected => "Your connection is secure",
                ConnectionStatus.Connecting => "Establishing secure connection...",
                ConnectionStatus.Disconnecting => "Closing secure connection...",
                ConnectionStatus.Reconnecting => "Attempting to reconnect...",
                ConnectionStatus.Error => LastErrorMessage ?? "Connection failed. Check logs.",
                _ => "Your connection is not secure"
            };
        }
    }

    public string ConnectButtonText => Status switch
    {
        ConnectionStatus.Connected => "Disconnect",
        ConnectionStatus.Connecting => "Cancel",
        ConnectionStatus.Disconnecting => "Cancel",
        ConnectionStatus.Reconnecting => "Cancel",
        ConnectionStatus.Error => "Retry",
        _ => "Connect"
    };

    public DashboardViewModel(IVpnConnectionService vpnService, 
                              IServerService serverService, 
                              PingService pingService, 
                              ISubscriptionService? subscriptionService = null,
                              IUserSettingsService? userSettingsService = null,
                              KillSwitchManager? killSwitchManager = null,
                              INotificationService? notificationService = null,
                              IAuthenticationService? authService = null,
                              IAccountPlanService? accountPlanService = null)
    {
        _vpnService = vpnService;
        _serverService = serverService;
        _pingService = pingService;
        _subscriptionService = subscriptionService;
        _userSettingsService = userSettingsService;
        _killSwitchManager = killSwitchManager;
        _notificationService = notificationService;
        _authService = authService;
        _accountPlanService = accountPlanService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        if (_userSettingsService != null)
        {
            _selectedProtocol = _userSettingsService.Settings.DefaultProtocol;
        }

        _vpnService.StatusChanged += OnStatusChanged;
        _vpnService.StatsUpdated += OnStatsUpdated;
        _vpnService.ErrorOccurred += OnErrorOccurred;

        WeakReferenceMessenger.Default.Register<ServerSelectedMessage>(this);
        WeakReferenceMessenger.Default.Register<SettingsChangedMessage>(this);
        if (_accountPlanService is not null)
        {
            _accountPlanService.PlanChanged += OnPlanChanged;
            Plan = _accountPlanService.CurrentPlan;
        }

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _refreshTimer.Tick += async (s, e) => await RefreshDataAsync();
        _refreshTimer.Start();

        if (_authService?.IsAuthenticated == true)
        {
            Plan = _accountPlanService?.CurrentPlan ?? _authService.Plan;
            _ = RefreshDataAsync();
        }

        _ = FetchUserIpAsync();
    }

    private async Task FetchUserIpAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var ip = await client.GetStringAsync("https://api.ipify.org");
            if (!string.IsNullOrWhiteSpace(ip))
            {
                _dispatcher.Invoke(() => UserIpAddress = ip.Trim());
            }
        }
        catch
        {
            // Ignore if we can't fetch the IP
        }
    }

    public void Receive(ServerSelectedMessage message)
    {
        SelectedServer = message.Value;
        SelectedProtocol = message.Protocol;
    }

    public void Receive(SettingsChangedMessage message)
    {
        if (Status == ConnectionStatus.Disconnected)
        {
            SelectedProtocol = message.Value.DefaultProtocol;
        }
    }

    public Task RefreshDataAsync()
    {
        lock (_refreshDataLock)
        {
            if (_refreshDataTask != null && !_refreshDataTask.IsCompleted)
                return _refreshDataTask;

            _refreshDataTask = RefreshDataInternalAsync();
            return _refreshDataTask;
        }
    }

    private async Task RefreshDataInternalAsync()
    {
        if (_subscriptionService is null)
            return;

        if (_authService?.IsAuthenticated == false)
            return;

        try
        {
            var authenticatedAtStart = _authService?.IsAuthenticated ?? false;

            if (_accountPlanService is not null)
            {
                await _accountPlanService.RefreshAsync();
                _dispatcher.Invoke(() =>
                {
                    Plan = _accountPlanService.CurrentPlan;
                });
            }
            else
            {
                var status = await _subscriptionService.GetStatusAsync();

                if (authenticatedAtStart != (_authService?.IsAuthenticated ?? false))
                    return;

                if (status is not null)
                {
                    _dispatcher.Invoke(() =>
                    {
                        Plan = status.IsPro ? UserPlan.Pro : UserPlan.Free;
                    });
                }
            }

            var quota = await _subscriptionService.GetQuotaAsync();

            if (authenticatedAtStart != (_authService?.IsAuthenticated ?? false))
                return;

            if (quota is not null)
            {
                _dispatcher.Invoke(() =>
                {
                    MonthlyDataUsedMb = quota.BytesUsed / (1024.0 * 1024.0);

                    var isUnlimited = quota.IsUnlimited || quota.BytesLimit is null;
                    var bytesLimit = quota.BytesLimit ?? 0;
                    if (bytesLimit <= 0 && !isUnlimited)
                    {
                        // Fallback to 5GB if API returns missing/invalid data for a limited plan.
                        bytesLimit = DefaultFreePlanLimitBytes;
                    }

                    MonthlyDataLimitMb = isUnlimited ? double.MaxValue : bytesLimit / (1024.0 * 1024.0);

                    CheckDataUsageThresholds();
                });
            }
        }
        catch
        {
            // Graceful degradation -- dashboard shows defaults (or previous values) until data arrives.
        }
    }

    [RelayCommand]
    private void DiscardSelection()
    {
        SelectedServer = null;
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        try
        {
            await ToggleConnectionCoreAsync();
        }
        catch (OperationCanceledException)
        {
            // User-driven cancellation should leave the service/status handlers in charge.
        }
        catch (Exception ex)
        {
            HandleConnectionCommandException(ex);
        }
    }

    private async Task ToggleConnectionCoreAsync()
    {
        if (Status is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Disconnecting)
        {
            await _vpnService.DisconnectAsync();
            return;
        }

        if (Status is ConnectionStatus.Disconnected or ConnectionStatus.Error)
        {
            var effectivePlan = await RefreshPlanForConnectionAsync();

            ServerLocation target;
            if (SelectedServer != null)
            {
                target = SelectedServer;
            }
            else
            {
                var servers = await _serverService.GetServersAsync();
                if (servers.Count == 0)
                {
                     // Ideally show a message, but for now just return
                     return;
                }

                var latencies = _pingService.GetCachedLatencies();
                if (latencies.Count == 0)
                {
                    IsMeasuringLatencies = true;
                    try
                    {
                        // Trigger latency measurement
                        var pingTargets = servers
                            .Where(s => !string.IsNullOrWhiteSpace(s.ServerHostname))
                            .Select(s => (s.ServerHostname!, (int?)s.LatencyPingPort));

                        latencies = await _pingService.MeasureLatenciesAsync(pingTargets);
                    }
                    finally
                    {
                        IsMeasuringLatencies = false;
                    }
                }

                var bestServer = ServerSelectionHelper.SelectBestServer(servers, latencies, effectivePlan == UserPlan.Pro);
                if (bestServer == null)
                {
                    // Fallback to first server if no best server found
                    target = servers.First();
                }
                else
                {
                    target = bestServer;
                }
            }

            // Enforce Free plan restriction: IKEv2 only
            var protocol = SelectedProtocol;
            if (effectivePlan == UserPlan.Free)
            {
                // Explicitly ensure IKEv2 is used for Free plan
                protocol = VpnProtocol.IKEv2;
            }

            if (!await EnsureQuotaAllowsConnectionAsync())
                return;

            if (_killSwitchManager != null)
            {
                var targetIp = target.ServerIp ?? target.ServerHostname;
                if (!string.IsNullOrWhiteSpace(targetIp))
                {
                    await _killSwitchManager.SetTargetServerIpAsync(targetIp);
                }
            }

            // Update connected info BEFORE connecting so it's available when StatusChanged event fires
            _dispatcher.Invoke(() =>
            {
                ConnectedCity = target.City;
                ConnectedCountry = target.Country;
                ConnectedFlag = target.Flag;
                ConnectedFlagUrl = target.FlagUrl ?? string.Empty;
                ConnectedServerName = target.ServerName;
            });

            await _vpnService.ConnectAsync(target, protocol);
        }
        else
        {
            await _vpnService.DisconnectAsync();
        }
    }

    private async Task<UserPlan> RefreshPlanForConnectionAsync()
    {
        if (_subscriptionService is not null)
        {
            try
            {
                var status = await _subscriptionService.GetStatusAsync();
                if (status is not null)
                {
                    var refreshedPlan = status.IsPro ? UserPlan.Pro : UserPlan.Free;
                    _dispatcher.Invoke(() => Plan = refreshedPlan);
                    return refreshedPlan;
                }
            }
            catch
            {
                // For connection-time protocol selection, do not trust stale Pro cache.
            }
        }

        if (_accountPlanService is not null)
        {
            await _accountPlanService.RefreshAsync(force: true);
            if (_accountPlanService.CurrentPlan == UserPlan.Free)
            {
                _dispatcher.Invoke(() => Plan = UserPlan.Free);
                return UserPlan.Free;
            }
        }

        if (Plan != UserPlan.Free)
            _dispatcher.Invoke(() => Plan = UserPlan.Free);

        return UserPlan.Free;
    }

    private async Task<bool> EnsureQuotaAllowsConnectionAsync()
    {
        if (_subscriptionService is null)
            return true;

        if (_authService?.IsAuthenticated == false)
            return true;

        CanConnectResponse? permission;
        try
        {
            permission = await _subscriptionService.CanConnectAsync();
        }
        catch
        {
            // Match backend fail-open behavior: do not block legitimate users when quota verification fails.
            return true;
        }

        if (permission is null || permission.Allowed)
            return true;

        var message = BuildQuotaDeniedMessage(permission);
        ShowConnectionError(message);

        _ = RefreshDataAsync();
        return false;
    }

    private static string BuildQuotaDeniedMessage(CanConnectResponse permission)
    {
        if (!string.IsNullOrWhiteSpace(permission.Message))
            return permission.Message;

        if (!string.IsNullOrWhiteSpace(permission.Reason))
            return permission.Reason;

        return "You have reached your monthly data limit. Upgrade to Pro for unlimited data or wait for your next reset.";
    }

    private void ShowConnectionError(string message)
    {
        _dispatcher.Invoke(() =>
        {
            LastErrorMessage = message;
            Status = ConnectionStatus.Error;
            _notificationService?.NotifyConnectionError(message);
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDescription));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(IsConnectionTransitionActive));
            OnPropertyChanged(nameof(ConnectionProgressText));
        });
    }

    private void OnPlanChanged()
    {
        if (_accountPlanService is null)
            return;

        if (_dispatcher.CheckAccess())
            Plan = _accountPlanService.CurrentPlan;
        else
            _dispatcher.Invoke(() => Plan = _accountPlanService.CurrentPlan);
    }

    [RelayCommand]
    private async Task QuickConnectAsync()
    {
        // Quick Connect is the same as ToggleConnection when no server is selected
        await ToggleConnectionAsync();
    }

    public async Task QuickConnectFromTrayAsync()
    {
        try
        {
            if (Status is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Disconnecting)
            {
                await _vpnService.DisconnectAsync();
                return;
            }

            SelectedServer = null;
            if (Status == ConnectionStatus.Connected)
                await _vpnService.DisconnectAsync();

            await ToggleConnectionCoreAsync();
        }
        catch (OperationCanceledException)
        {
            // Status handlers own the visible state transition.
        }
        catch (Exception ex)
        {
            HandleConnectionCommandException(ex);
        }
    }

    public async Task DisconnectFromTrayAsync()
    {
        try
        {
            if (Status != ConnectionStatus.Disconnected)
                await _vpnService.DisconnectAsync();
        }
        catch (OperationCanceledException)
        {
            // Status handlers own the visible state transition.
        }
        catch (Exception ex)
        {
            HandleConnectionCommandException(ex);
        }
    }

    public async Task ConnectToServerFromTrayAsync(ServerLocation server)
    {
        ArgumentNullException.ThrowIfNull(server);

        try
        {
            if (Status is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Disconnecting)
                await _vpnService.DisconnectAsync();
            else if (Status == ConnectionStatus.Connected)
                await _vpnService.DisconnectAsync();

            SelectedServer = server;
            await ToggleConnectionCoreAsync();
        }
        catch (OperationCanceledException)
        {
            // Status handlers own the visible state transition.
        }
        catch (Exception ex)
        {
            HandleConnectionCommandException(ex);
        }
    }

    private void HandleConnectionCommandException(Exception ex)
    {
        var message = _vpnService.LastErrorMessage ?? WinVpnConnectionService.ClassifyConnectionError(ex);

        _dispatcher.Invoke(() =>
        {
            LastErrorMessage = message;
            Status = ConnectionStatus.Error;
            _notificationService?.NotifyConnectionError(message);
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDescription));
            OnPropertyChanged(nameof(ConnectButtonText));
        });
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status) =>
        _dispatcher.Invoke(() =>
        {
            var previous = _previousStatus;
            _previousStatus = status;
            Status = status;
            VpnIpAddress = _vpnService.VpnIpAddress;
            if (status != ConnectionStatus.Error)
                LastErrorMessage = null;

            if (status == ConnectionStatus.Disconnected)
            {
                _ = FetchUserIpAsync();
            }

            if (status is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting)
            {
                if (previous is not (ConnectionStatus.Connecting or ConnectionStatus.Reconnecting))
                    _notificationService?.NotifyVpnConnecting();
            }
            else if (status == ConnectionStatus.Connected && previous != ConnectionStatus.Connected)
            {
                var ipAddress = VpnIpAddress ?? SelectedServer?.ServerIp;
                _notificationService?.NotifyVpnConnected(
                    ConnectedServerName, ConnectedCity, ConnectedCountry, ipAddress);

                // Reset data-threshold flags so warnings can fire again in the new session.
                _dataWarning80Fired = false;
                _dataLimitFired = false;
            }
            else if (status == ConnectionStatus.Disconnected && previous != ConnectionStatus.Disconnected)
            {
                _notificationService?.NotifyVpnDisconnected();
            }
            else if (status == ConnectionStatus.Error && previous == ConnectionStatus.Connected)
            {
                // Error message will be populated by OnErrorOccurred; suppress duplicate here.
            }

            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDescription));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(IsConnectionTransitionActive));
            OnPropertyChanged(nameof(ConnectionProgressText));
        });

    private void OnErrorOccurred(object? sender, string message) =>
        _dispatcher.Invoke(() =>
        {
            LastErrorMessage = message;
            _notificationService?.NotifyConnectionError(message);
            OnPropertyChanged(nameof(StatusDescription));
        });

    private void OnStatsUpdated(object? sender, ConnectionStats stats) =>
        _dispatcher.Invoke(() =>
        {
            DownloadSpeed = stats.DownloadSpeedMbps;
            UploadSpeed = stats.UploadSpeedMbps;
            SessionDataMb = stats.SessionDataMb;
            ConnectionTime = stats.Duration.ToString(@"hh\:mm\:ss");
            if (VpnIpAddress is null)
                VpnIpAddress = _vpnService.VpnIpAddress;

            // Check bandwidth thresholds as session data grows
            CheckDataUsageThresholds();
        });

    /// <summary>
    /// Fires data-usage notifications at the 80 % and 100 % thresholds.
    /// Each threshold fires at most once per session (flags reset on reconnect).
    /// Only fires for limited plans.
    /// </summary>
    private void CheckDataUsageThresholds()
    {
        if (_notificationService is null || !IsDataLimited || MonthlyDataLimitMb <= 0)
            return;

        var percent = MonthlyUsagePercent;

        if (!_dataLimitFired && percent >= 100.0)
        {
            _dataLimitFired = true;
            _dataWarning80Fired = true;
            _notificationService.NotifyDataLimitReached();
        }
        else if (!_dataWarning80Fired && percent >= 80.0)
        {
            _dataWarning80Fired = true;
            _notificationService.NotifyDataUsageWarning(percent);
        }
    }
}
