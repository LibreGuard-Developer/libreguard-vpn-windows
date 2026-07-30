using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Tests.ViewModels;

public sealed class DashboardViewModelTests
{
    [Fact]
    public async Task ToggleConnectionCommand_WhenConnectThrows_DoesNotRethrowAndShowsError()
    {
        var vpnService = new ThrowingVpnConnectionService();
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            notificationService: notifications)
        {
            SelectedServer = new ServerLocation(
                id: "3",
                country: "Testland",
                city: "Test City",
                serverName: "Test Server",
                flag: "",
                flagUrl: null,
                pingMs: 1,
                loadPercent: 10,
                serverIp: "127.0.0.1")
        };

        var exception = await Record.ExceptionAsync(() => viewModel.ToggleConnectionCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Equal(ConnectionStatus.Error, viewModel.Status);
        Assert.Contains("simulated connect failure", viewModel.LastErrorMessage);
        Assert.Contains("simulated connect failure", notifications.LastConnectionError);
    }

    [Fact]
    public async Task RefreshDataAsync_WithUnlimitedProQuota_DisplaysUnlimitedWithoutThrowing()
    {
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = true, Plan = "Pro" },
            new DataQuotaResponse
            {
                BytesUsed = 12345,
                BytesLimit = null,
                BytesRemaining = null,
                UsagePercentage = null,
                IsUnlimited = true,
                IsOverLimit = false
            });
        var viewModel = new DashboardViewModel(
            new IdleVpnConnectionService(),
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription);

        await viewModel.RefreshDataAsync();

        Assert.Equal(UserPlan.Pro, viewModel.Plan);
        Assert.Equal(double.MaxValue, viewModel.MonthlyDataLimitMb);
        Assert.False(viewModel.IsDataLimited);
        Assert.Equal("Unlimited", viewModel.MonthlyDataRemainingText);
    }

    [Fact]
    public async Task RefreshDataAsync_WithLimitedQuota_ComputesUsageValues()
    {
        const long oneGb = 1024L * 1024 * 1024;
        const long usedBytes = 256L * 1024 * 1024;
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = false, Plan = "Free" },
            new DataQuotaResponse
            {
                BytesUsed = usedBytes,
                BytesLimit = oneGb,
                BytesRemaining = oneGb - usedBytes,
                UsagePercentage = 25,
                IsUnlimited = false,
                IsOverLimit = false
            });
        var viewModel = new DashboardViewModel(
            new IdleVpnConnectionService(),
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription);

        await viewModel.RefreshDataAsync();

        Assert.Equal(UserPlan.Free, viewModel.Plan);
        Assert.Equal(256, viewModel.MonthlyDataUsedMb);
        Assert.Equal(1024, viewModel.MonthlyDataLimitMb);
        Assert.Equal(25, viewModel.MonthlyUsagePercent);
        Assert.True(viewModel.IsDataLimited);
    }

    [Fact]
    public async Task ToggleConnectionCommand_WhenSubscriptionStatusUnavailableAndCachedPro_UsesIkev2Protocol()
    {
        var vpnService = new RecordingVpnConnectionService();
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            subscriptionService: new StaticSubscriptionService(null, null),
            authService: new StaticAuthenticationService(UserPlan.Pro))
        {
            SelectedServer = new ServerLocation(
                id: "pro-1",
                country: "Testland",
                city: "Pro City",
                serverName: "Pro Server",
                flag: "",
                flagUrl: null,
                pingMs: 1,
                loadPercent: 10,
                isPremium: true,
                serverIp: "127.0.0.1"),
            SelectedProtocol = VpnProtocol.OpenVPN
        };

        await viewModel.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(VpnProtocol.IKEv2, vpnService.LastProtocol);
    }

    [Fact]
    public async Task ToggleConnectionCommand_WhenCanConnectDenied_BlocksConnectionAndShowsBackendMessage()
    {
        var vpnService = new RecordingVpnConnectionService();
        var notifications = new RecordingNotificationService();
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = false, Plan = "Free" },
            new DataQuotaResponse
            {
                BytesUsed = 6L * 1024 * 1024 * 1024,
                BytesLimit = 5L * 1024 * 1024 * 1024,
                IsUnlimited = false,
                IsOverLimit = true
            },
            new CanConnectResponse
            {
                Allowed = false,
                Reason = "Data limit exceeded for this billing period",
                Message = "You have used 6.00 GB of your 5.00 GB monthly limit."
            });
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription,
            notificationService: notifications)
        {
            SelectedServer = new ServerLocation(
                id: "3",
                country: "Germany",
                city: "Berlin",
                serverName: "DE-Berlin-01",
                flag: "",
                flagUrl: null,
                pingMs: 1,
                loadPercent: 10,
                serverIp: "127.0.0.1")
        };

        await viewModel.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(0, vpnService.ConnectCalls);
        Assert.Equal(ConnectionStatus.Error, viewModel.Status);
        Assert.Equal("You have used 6.00 GB of your 5.00 GB monthly limit.", viewModel.LastErrorMessage);
        Assert.Equal(viewModel.LastErrorMessage, notifications.LastConnectionError);
    }

    [Fact]
    public async Task ToggleConnectionCommand_WhenFreshStatusIsFree_ForcesIkev2EvenIfOpenVpnSelected()
    {
        var vpnService = new RecordingVpnConnectionService();
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = false, Plan = "Free" },
            quota: null,
            new CanConnectResponse { Allowed = true });
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription)
        {
            SelectedServer = new ServerLocation(
                id: "3",
                country: "Germany",
                city: "Berlin",
                serverName: "DE-Berlin-01",
                flag: "",
                flagUrl: null,
                pingMs: 1,
                loadPercent: 10,
                serverIp: "127.0.0.1"),
            SelectedProtocol = VpnProtocol.OpenVPN
        };

        await viewModel.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(VpnProtocol.IKEv2, vpnService.LastProtocol);
    }

    [Fact]
    public async Task ToggleConnectionCommand_WhenFreshStatusIsPro_AllowsOpenVpnProtocol()
    {
        var vpnService = new RecordingVpnConnectionService();
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = true, Plan = "Pro" },
            quota: null,
            new CanConnectResponse { Allowed = true });
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription)
        {
            SelectedServer = new ServerLocation(
                id: "3",
                country: "Germany",
                city: "Berlin",
                serverName: "DE-Berlin-01",
                flag: "",
                flagUrl: null,
                pingMs: 1,
                loadPercent: 10,
                serverIp: "127.0.0.1"),
            SelectedProtocol = VpnProtocol.OpenVPN
        };

        await viewModel.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(VpnProtocol.OpenVPN, vpnService.LastProtocol);
    }

    [Fact]
    public async Task ToggleConnectionCommand_WhenCanConnectAllowed_ProceedsWithConnection()
    {
        var vpnService = new RecordingVpnConnectionService();
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = false, Plan = "Free" },
            quota: null,
            new CanConnectResponse { Allowed = true });
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription)
        {
            SelectedServer = new ServerLocation(
                id: "3",
                country: "Germany",
                city: "Berlin",
                serverName: "DE-Berlin-01",
                flag: "",
                flagUrl: null,
                pingMs: 1,
                loadPercent: 10,
                serverIp: "127.0.0.1")
        };

        await viewModel.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(1, vpnService.ConnectCalls);
        Assert.Equal(ConnectionStatus.Connected, viewModel.Status);
    }

    [Fact]
    public async Task ToggleConnectionCommand_WhenCanConnectCheckFails_ProceedsFailOpen()
    {
        var vpnService = new RecordingVpnConnectionService();
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = false, Plan = "Free" },
            quota: null,
            canConnect: null,
            throwOnCanConnect: true);
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription)
        {
            SelectedServer = new ServerLocation(
                id: "3",
                country: "Germany",
                city: "Berlin",
                serverName: "DE-Berlin-01",
                flag: "",
                flagUrl: null,
                pingMs: 1,
                loadPercent: 10,
                serverIp: "127.0.0.1")
        };

        await viewModel.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(1, vpnService.ConnectCalls);
        Assert.Equal(ConnectionStatus.Connected, viewModel.Status);
    }

    [Fact]
    public async Task QuickConnectFromTrayAsync_ClearsSelectedServerAndConnectsBestServer()
    {
        var vpnService = new RecordingVpnConnectionService();
        var bestServer = new ServerLocation(
            id: "best-1",
            country: "Germany",
            city: "Berlin",
            serverName: "DE-Berlin-01",
            flag: "",
            flagUrl: null,
            pingMs: 10,
            loadPercent: 15,
            serverIp: "127.0.0.10");
        var viewModel = new DashboardViewModel(
            vpnService,
            new StaticServerService(bestServer),
            new PingService())
        {
            SelectedServer = new ServerLocation(
                id: "old-1",
                country: "France",
                city: "Paris",
                serverName: "FR-Paris-01",
                flag: "",
                flagUrl: null,
                pingMs: 20,
                loadPercent: 20,
                serverIp: "127.0.0.20")
        };

        await viewModel.QuickConnectFromTrayAsync();

        Assert.Null(viewModel.SelectedServer);
        Assert.Equal(1, vpnService.ConnectCalls);
        Assert.Equal("best-1", vpnService.LastServer?.Id);
    }

    [Fact]
    public async Task ConnectToServerFromTrayAsync_WhenConnected_DisconnectsThenReconnectsToRequestedServer()
    {
        var vpnService = new RecordingVpnConnectionService();
        var requestedServer = new ServerLocation(
            id: "target-1",
            country: "Netherlands",
            city: "Amsterdam",
            serverName: "NL-Amsterdam-01",
            flag: "",
            flagUrl: null,
            pingMs: 15,
            loadPercent: 12,
            serverIp: "127.0.0.30");
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService())
        {
            Status = ConnectionStatus.Connected
        };

        await viewModel.ConnectToServerFromTrayAsync(requestedServer);

        Assert.Equal(1, vpnService.DisconnectCalls);
        Assert.Equal(1, vpnService.ConnectCalls);
        Assert.Equal("target-1", vpnService.LastServer?.Id);
        Assert.Equal(requestedServer, viewModel.SelectedServer);
    }

    [Fact]
    public void DisconnectingStatus_MapsToTransitionCopyAndCancelButton()
    {
        var viewModel = new DashboardViewModel(
            new IdleVpnConnectionService(),
            new EmptyServerService(),
            new PingService())
        {
            Status = ConnectionStatus.Disconnecting
        };

        Assert.Equal("Disconnecting", viewModel.StatusText);
        Assert.Equal("Closing secure connection...", viewModel.StatusDescription);
        Assert.Equal("Cancel", viewModel.ConnectButtonText);
        Assert.True(viewModel.IsConnectionTransitionActive);
        Assert.Equal("Closing tunnel", viewModel.ConnectionProgressText);
    }

    [Fact]
    public async Task OnConnect_SendsNotificationWithIpAddress()
    {
        var vpnService = new RecordingVpnConnectionService { VpnIpToReturn = "10.8.0.5" };
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            notificationService: notifications)
        {
            SelectedServer = new ServerLocation(
                id: "test-1",
                country: "Germany",
                city: "Berlin",
                serverName: "DE-Berlin-01",
                flag: "🇩🇪",
                flagUrl: null,
                pingMs: 15,
                loadPercent: 20,
                serverIp: "192.168.1.1")
        };

        await viewModel.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal("DE-Berlin-01", notifications.LastConnectedServerName);
        Assert.Equal("Berlin", notifications.LastConnectedCity);
        Assert.Equal("Germany", notifications.LastConnectedCountry);
        Assert.Equal("10.8.0.5", notifications.LastConnectedIpAddress);
    }

    [Fact]
    public async Task OnConnect_WithNoVpnIp_UsesServerIpInNotification()
    {
        var vpnService = new RecordingVpnConnectionService { VpnIpToReturn = null };
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            notificationService: notifications)
        {
            SelectedServer = new ServerLocation(
                id: "test-2",
                country: "France",
                city: "Paris",
                serverName: "FR-Paris-01",
                flag: "🇫🇷",
                flagUrl: null,
                pingMs: 20,
                loadPercent: 30,
                serverIp: "192.168.2.1")
        };

        await viewModel.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal("192.168.2.1", notifications.LastConnectedIpAddress);
    }

    [Fact]
    public void OnConnecting_SendsConnectingNotification()
    {
        var vpnService = new ControllableVpnConnectionService();
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            notificationService: notifications);

        vpnService.SimulateStatusChange(ConnectionStatus.Connecting);

        Assert.True(notifications.ConnectingCalled);
    }

    [Fact]
    public void OnDisconnect_SendsDisconnectedNotification()
    {
        var vpnService = new ControllableVpnConnectionService();
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            notificationService: notifications);

        vpnService.SimulateStatusChange(ConnectionStatus.Connected);
        vpnService.SimulateStatusChange(ConnectionStatus.Disconnecting);
        vpnService.SimulateStatusChange(ConnectionStatus.Disconnected);

        Assert.True(notifications.DisconnectedCalled);
    }

    [Fact]
    public async Task DataUsage_At80Percent_SendsWarningNotification()
    {
        const long tenGb = 10L * 1024 * 1024 * 1024;
        const long eightGb = 8L * 1024 * 1024 * 1024;
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = false, Plan = "Free" },
            new DataQuotaResponse
            {
                BytesUsed = eightGb,
                BytesLimit = tenGb,
                BytesRemaining = tenGb - eightGb,
                UsagePercentage = 80,
                IsUnlimited = false,
                IsOverLimit = false
            });
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            new IdleVpnConnectionService(),
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription,
            notificationService: notifications);

        await viewModel.RefreshDataAsync();

        Assert.NotNull(notifications.LastDataWarningPercent);
        Assert.Equal(80.0, notifications.LastDataWarningPercent.Value, precision: 1);
        Assert.False(notifications.DataLimitReachedCalled);
    }

    [Fact]
    public async Task DataUsage_At100Percent_SendsLimitReachedNotification()
    {
        const long tenGb = 10L * 1024 * 1024 * 1024;
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = false, Plan = "Free" },
            new DataQuotaResponse
            {
                BytesUsed = tenGb,
                BytesLimit = tenGb,
                BytesRemaining = 0,
                UsagePercentage = 100,
                IsUnlimited = false,
                IsOverLimit = false
            });
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            new IdleVpnConnectionService(),
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription,
            notificationService: notifications);

        await viewModel.RefreshDataAsync();

        Assert.True(notifications.DataLimitReachedCalled);
    }

    [Fact]
    public void SessionDataGrowth_TriggersThresholdCheck()
    {
        const long tenGb = 10L * 1024 * 1024 * 1024;
        const long sevenGb = 7L * 1024 * 1024 * 1024;
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = false, Plan = "Free" },
            new DataQuotaResponse
            {
                BytesUsed = sevenGb,
                BytesLimit = tenGb,
                BytesRemaining = tenGb - sevenGb,
                UsagePercentage = 70,
                IsUnlimited = false,
                IsOverLimit = false
            });
        var vpnService = new ControllableVpnConnectionService();
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            vpnService,
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription,
            notificationService: notifications);

        // Load initial quota (70% used)
        viewModel.RefreshDataAsync().Wait();
        Assert.Null(notifications.LastDataWarningPercent);

        // Simulate session data pushing usage to 81%
        // 7GB existing + 1.1GB session = 8.1GB / 10GB = 81%
        vpnService.SimulateStatsUpdate(new ConnectionStats(0, 0, 1126.4, TimeSpan.Zero));

        Assert.NotNull(notifications.LastDataWarningPercent);
        Assert.True(notifications.LastDataWarningPercent >= 80.0);
    }

    [Fact]
    public async Task ProPlan_DoesNotSendDataLimitNotifications()
    {
        var subscription = new StaticSubscriptionService(
            new SubscriptionStatusResponse { IsPro = true, Plan = "Pro" },
            new DataQuotaResponse
            {
                BytesUsed = 999999999999,
                BytesLimit = null,
                BytesRemaining = null,
                UsagePercentage = null,
                IsUnlimited = true,
                IsOverLimit = false
            });
        var notifications = new RecordingNotificationService();
        var viewModel = new DashboardViewModel(
            new IdleVpnConnectionService(),
            new EmptyServerService(),
            new PingService(),
            subscriptionService: subscription,
            notificationService: notifications);

        await viewModel.RefreshDataAsync();

        Assert.Null(notifications.LastDataWarningPercent);
        Assert.False(notifications.DataLimitReachedCalled);
    }

    private sealed class ThrowingVpnConnectionService : IVpnConnectionService
    {
        public ConnectionStatus Status => ConnectionStatus.Disconnected;
        public ConnectionStats? CurrentStats => null;
        public string? VpnIpAddress => null;
        public string? LastErrorMessage => null;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<ConnectionStats>? StatsUpdated;

        public Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("simulated connect failure");
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void RaiseUnusedEvents()
        {
            StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
            ErrorOccurred?.Invoke(this, string.Empty);
            StatsUpdated?.Invoke(this, new ConnectionStats(0, 0, 0, TimeSpan.Zero));
        }
    }

    private sealed class IdleVpnConnectionService : IVpnConnectionService
    {
        public ConnectionStatus Status => ConnectionStatus.Disconnected;
        public ConnectionStats? CurrentStats => null;
        public string? VpnIpAddress => null;
        public string? LastErrorMessage => null;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<ConnectionStats>? StatsUpdated;

        public Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default)
        {
            StatusChanged?.Invoke(this, ConnectionStatus.Connected);
            StatsUpdated?.Invoke(this, new ConnectionStats(0, 0, 0, TimeSpan.Zero));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            ErrorOccurred?.Invoke(this, string.Empty);
            StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingVpnConnectionService : IVpnConnectionService
    {
        public ConnectionStatus Status => ConnectionStatus.Disconnected;
        public ConnectionStats? CurrentStats => null;
        public string? VpnIpAddress => VpnIpToReturn;
        public string? LastErrorMessage => null;
        public VpnProtocol? LastProtocol { get; private set; }
        public ServerLocation? LastServer { get; private set; }
        public string? VpnIpToReturn { get; set; }
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<ConnectionStats>? StatsUpdated;

        public Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            LastProtocol = protocol;
            LastServer = server;
            StatusChanged?.Invoke(this, ConnectionStatus.Connected);
            StatsUpdated?.Invoke(this, new ConnectionStats(0, 0, 0, TimeSpan.Zero));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            ErrorOccurred?.Invoke(this, string.Empty);
            StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
            return Task.CompletedTask;
        }
    }

    private sealed class ControllableVpnConnectionService : IVpnConnectionService
    {
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
        public ConnectionStats? CurrentStats => null;
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
            StatsUpdated?.Invoke(this, stats);
        }

        public Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default)
        {
            SimulateStatusChange(ConnectionStatus.Connected);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            SimulateStatusChange(ConnectionStatus.Disconnecting);
            SimulateStatusChange(ConnectionStatus.Disconnected);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticSubscriptionService : ISubscriptionService
    {
        private readonly SubscriptionStatusResponse? _status;
        private readonly DataQuotaResponse? _quota;
        private readonly CanConnectResponse? _canConnect;
        private readonly bool _throwOnCanConnect;

        public StaticSubscriptionService(
            SubscriptionStatusResponse? status,
            DataQuotaResponse? quota,
            CanConnectResponse? canConnect = null,
            bool throwOnCanConnect = false)
        {
            _status = status;
            _quota = quota;
            _canConnect = canConnect;
            _throwOnCanConnect = throwOnCanConnect;
        }

        public Task<SubscriptionStatusResponse?> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult<SubscriptionStatusResponse?>(_status);

        public Task<DataQuotaResponse?> GetQuotaAsync(CancellationToken ct = default) =>
            Task.FromResult<DataQuotaResponse?>(_quota);

        public Task<CanConnectResponse?> CanConnectAsync(CancellationToken ct = default)
        {
            if (_throwOnCanConnect)
                throw new HttpRequestException("quota check unavailable");

            return Task.FromResult<CanConnectResponse?>(_canConnect ?? new CanConnectResponse { Allowed = true });
        }

        public Task<bool> ValidateTokenAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<MoneroPriceResponse?> GetMoneroPriceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) =>
            Task.FromResult<MoneroPriceResponse?>(null);

        public Task<MoneroInvoiceResponse?> CreateMoneroInvoiceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) =>
            Task.FromResult<MoneroInvoiceResponse?>(null);

        public Task<MoneroStatusResponse?> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken ct = default) =>
            Task.FromResult<MoneroStatusResponse?>(null);

        public Task<MoneroInvoiceResponse?> GetLatestMoneroInvoiceAsync(CancellationToken ct = default) =>
            Task.FromResult<MoneroInvoiceResponse?>(null);

        public Task<CreemCheckoutResponse?> CreateCreemCheckoutAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) =>
            Task.FromResult<CreemCheckoutResponse?>(null);

        public Task<CreemPaymentStatusResponse?> GetCreemPaymentStatusAsync(string transactionId, CancellationToken ct = default) =>
            Task.FromResult<CreemPaymentStatusResponse?>(null);

        public Task<CreemPaymentVerifyResponse?> VerifyCreemPaymentAsync(string transactionId, CancellationToken ct = default) =>
            Task.FromResult<CreemPaymentVerifyResponse?>(null);
    }

    private sealed class StaticAuthenticationService : IAuthenticationService
    {
        public StaticAuthenticationService(UserPlan plan)
        {
            Plan = plan;
        }

        public event Action? SessionChanged;

        public bool IsAuthenticated => true;
        public string? UserEmail => "pro@example.test";
        public string? UserId => "user-1";
        public UserPlan Plan { get; }

        public Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> Verify2FaAsync(string email, string code, string? pendingLoginToken = null, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithGoogleAsync(GoogleLoginContext loginContext, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithOAuthAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithTokenAsync(string token, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<bool> CheckEmailConfirmationAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ResendConfirmationAsync(string email, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthAsync(string email, string password, int deviceIdToRemove, CancellationToken cancellationToken = default) => Task.FromResult(PreAuthDeviceRemovalResult.Ok());
        public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthOAuthAsync(GoogleLoginContext loginContext, string provider, int deviceIdToRemove, CancellationToken cancellationToken = default) => Task.FromResult(PreAuthDeviceRemovalResult.Ok());
        public Task<TwoFactorSetupResponse?> InitiateTwoFactorSetupAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorSetupResponse?>(null);
        public Task<TwoFactorEnableResponse?> VerifyAndEnableTwoFactorAsync(string code, CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorEnableResponse?>(null);
        public Task<TwoFactorRecoveryCodesResponse?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorRecoveryCodesResponse?>(null);
        public Task<TwoFactorDisableResponse?> DisableTwoFactorAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorDisableResponse?>(null);
        public Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PasswordResetResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default) => Task.FromResult(PasswordResetResult.Ok("ok"));
        public Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorStatusResponse?>(null);
        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            SessionChanged?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServerService : IServerService
    {
        public Task<IReadOnlyList<ServerLocation>> GetServersAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ServerLocation>>([]);
        }

        public IReadOnlyList<string> GetFavorites() => [];
        public void ToggleFavorite(string serverId) { }
        public IReadOnlyList<string> GetRecent() => [];
        public void AddRecent(string serverId) { }
    }

    private sealed class StaticServerService(params ServerLocation[] servers) : IServerService
    {
        private readonly IReadOnlyList<ServerLocation> _servers = servers;

        public Task<IReadOnlyList<ServerLocation>> GetServersAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_servers);
        }

        public IReadOnlyList<string> GetFavorites() => [];
        public void ToggleFavorite(string serverId) { }
        public IReadOnlyList<string> GetRecent() => [];
        public void AddRecent(string serverId) { }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public string LastConnectionError { get; private set; } = string.Empty;
        public string? LastConnectedServerName { get; private set; }
        public string? LastConnectedCity { get; private set; }
        public string? LastConnectedCountry { get; private set; }
        public string? LastConnectedIpAddress { get; private set; }
        public bool ConnectingCalled { get; private set; }
        public bool DisconnectedCalled { get; private set; }
        public bool ConnectionLostCalled { get; private set; }
        public double? LastDataWarningPercent { get; private set; }
        public bool DataLimitReachedCalled { get; private set; }

        public void NotifyVpnConnecting() => ConnectingCalled = true;

        public void NotifyVpnConnected(string serverName, string city, string country, string? ipAddress)
        {
            LastConnectedServerName = serverName;
            LastConnectedCity = city;
            LastConnectedCountry = country;
            LastConnectedIpAddress = ipAddress;
        }

        public void NotifyVpnDisconnected() => DisconnectedCalled = true;
        public void NotifyConnectionLost() => ConnectionLostCalled = true;
        public void NotifyConnectionError(string message) => LastConnectionError = message;
        public void NotifyKillSwitchEnabled() { }
        public void NotifyKillSwitchDisabled() { }
        public void NotifyDataUsageWarning(double percentUsed) => LastDataWarningPercent = percentUsed;
        public void NotifyDataLimitReached() => DataLimitReachedCalled = true;
    }
}
