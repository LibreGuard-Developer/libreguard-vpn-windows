using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Tests.ViewModels;

public sealed class ServerListViewModelTests
{
    [Fact]
    public async Task Constructor_WhenSubscriptionStatusUnavailableAndCachedPro_KeepsOpenVpnAvailable()
    {
        var navigation = new RecordingNavigationService();
        var viewModel = new ServerListViewModel(
            new EmptyServerService(),
            navigation,
            new IdleVpnConnectionService(),
            new StaticUserSettingsService(new UserSettings { DefaultProtocol = VpnProtocol.OpenVPN }),
            new StaticAccountPlanService(UserPlan.Pro));

        await Task.Delay(50);

        Assert.True(viewModel.IsOpenVpnAvailable);
        Assert.Equal(VpnProtocol.OpenVPN, viewModel.SelectedProtocol);
        Assert.NotEqual("upgrade", navigation.CurrentView);
    }

    [Fact]
    public async Task Constructor_WhenBackendConfirmsFree_DowngradesOpenVpnSelection()
    {
        var viewModel = new ServerListViewModel(
            new EmptyServerService(),
            new RecordingNavigationService(),
            new IdleVpnConnectionService(),
            new StaticUserSettingsService(new UserSettings { DefaultProtocol = VpnProtocol.OpenVPN }),
            new StaticAccountPlanService(UserPlan.Free));

        await Task.Delay(50);

        Assert.False(viewModel.IsOpenVpnAvailable);
        Assert.Equal(VpnProtocol.IKEv2, viewModel.SelectedProtocol);
    }

    [Fact]
    public async Task RefreshPlanAsync_WhenPlanBecomesPro_UnlocksOpenVpnWithoutRecreatingViewModel()
    {
        var accountPlan = new StaticAccountPlanService(UserPlan.Free);
        var viewModel = new ServerListViewModel(
            new EmptyServerService(),
            new RecordingNavigationService(),
            new IdleVpnConnectionService(),
            new StaticUserSettingsService(new UserSettings { DefaultProtocol = VpnProtocol.IKEv2 }),
            accountPlan);

        Assert.False(viewModel.IsOpenVpnAvailable);

        accountPlan.NextRefreshPlan = UserPlan.Pro;
        await viewModel.RefreshPlanAsync(force: true);

        Assert.True(viewModel.IsOpenVpnAvailable);
    }

    private sealed class StaticSubscriptionService : ISubscriptionService
    {
        private readonly SubscriptionStatusResponse? _status;

        public StaticSubscriptionService(SubscriptionStatusResponse? status)
        {
            _status = status;
        }

        public Task<SubscriptionStatusResponse?> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(_status);
        public Task<DataQuotaResponse?> GetQuotaAsync(CancellationToken ct = default) => Task.FromResult<DataQuotaResponse?>(null);
        public Task<CanConnectResponse?> CanConnectAsync(CancellationToken ct = default) =>
            Task.FromResult<CanConnectResponse?>(new CanConnectResponse { Allowed = true });
        public Task<bool> ValidateTokenAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MoneroPriceResponse?> GetMoneroPriceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) => Task.FromResult<MoneroPriceResponse?>(null);
        public Task<MoneroInvoiceResponse?> CreateMoneroInvoiceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) => Task.FromResult<MoneroInvoiceResponse?>(null);
        public Task<MoneroStatusResponse?> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken ct = default) => Task.FromResult<MoneroStatusResponse?>(null);
        public Task<MoneroInvoiceResponse?> GetLatestMoneroInvoiceAsync(CancellationToken ct = default) => Task.FromResult<MoneroInvoiceResponse?>(null);
        public Task<CreemCheckoutResponse?> CreateCreemCheckoutAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) => Task.FromResult<CreemCheckoutResponse?>(null);
        public Task<CreemPaymentStatusResponse?> GetCreemPaymentStatusAsync(string transactionId, CancellationToken ct = default) => Task.FromResult<CreemPaymentStatusResponse?>(null);
        public Task<CreemPaymentVerifyResponse?> VerifyCreemPaymentAsync(string transactionId, CancellationToken ct = default) => Task.FromResult<CreemPaymentVerifyResponse?>(null);
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

    private sealed class StaticAccountPlanService : IAccountPlanService
    {
        public StaticAccountPlanService(UserPlan plan)
        {
            CurrentPlan = plan;
        }

        public UserPlan CurrentPlan { get; private set; }
        public UserPlan? NextRefreshPlan { get; set; }
        public bool IsPro => CurrentPlan == UserPlan.Pro;
        public bool IsOpenVpnAvailable => IsPro;
        public string CurrentPlanLabel => IsPro ? "Pro" : "Free";
        public bool IsRefreshing => false;
        public event Action? PlanChanged;

        public Task RefreshAsync(bool force = false, CancellationToken ct = default)
        {
            if (NextRefreshPlan is { } plan)
            {
                CurrentPlan = plan;
                PlanChanged?.Invoke();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StaticUserSettingsService : IUserSettingsService
    {
        public StaticUserSettingsService(UserSettings settings)
        {
            Settings = settings;
        }

        public UserSettings Settings { get; }
        public event EventHandler? SettingsChanged;
        public Task SaveSettingsAsync()
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task LoadSettingsAsync() => Task.CompletedTask;
    }

    private sealed class RecordingNavigationService : INavigationService
    {
        public string CurrentView { get; private set; } = "servers";
        public event EventHandler<string>? ViewChanged;
        public void NavigateTo(string viewKey)
        {
            CurrentView = viewKey;
            ViewChanged?.Invoke(this, viewKey);
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

    private sealed class EmptyServerService : IServerService
    {
        public Task<IReadOnlyList<ServerLocation>> GetServersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ServerLocation>>([]);

        public IReadOnlyList<string> GetFavorites() => [];
        public void ToggleFavorite(string serverId) { }
        public IReadOnlyList<string> GetRecent() => [];
        public void AddRecent(string serverId) { }
    }
}
