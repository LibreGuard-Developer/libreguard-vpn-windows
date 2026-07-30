using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class KillSwitchManagerTests
{
    [Fact]
    public async Task InitializeAsync_WhenKillSwitchDisabled_DoesNotShowRestoredNotification()
    {
        var killSwitch = new RecordingKillSwitchService();
        var settings = new TestUserSettingsService { Settings = { KillSwitch = false } };
        var notifications = new RecordingNotificationService();
        using var manager = CreateManager(killSwitch, settings, notifications);

        await manager.InitializeAsync();

        Assert.Equal(1, killSwitch.DisableCalls);
        Assert.Equal(0, notifications.KillSwitchDisabledCalls);
        Assert.Equal(0, notifications.KillSwitchEnabledCalls);
    }

    [Fact]
    public async Task InitializeAsync_WhenKillSwitchEnabled_AppliesWithoutShowingActiveNotification()
    {
        var killSwitch = new RecordingKillSwitchService();
        var settings = new TestUserSettingsService { Settings = { KillSwitch = true } };
        var notifications = new RecordingNotificationService();
        using var manager = CreateManager(killSwitch, settings, notifications);

        await manager.InitializeAsync();

        Assert.Equal(1, killSwitch.EnableCalls);
        Assert.Equal(0, notifications.KillSwitchEnabledCalls);
        Assert.Equal(0, notifications.KillSwitchDisabledCalls);
    }

    [Fact]
    public async Task SettingsChange_WhenKillSwitchTurnsOffAfterInitialization_ShowsRestoredNotification()
    {
        var killSwitch = new RecordingKillSwitchService();
        var settings = new TestUserSettingsService { Settings = { KillSwitch = true } };
        var notifications = new RecordingNotificationService();
        using var manager = CreateManager(killSwitch, settings, notifications);
        await manager.InitializeAsync();

        settings.Settings.KillSwitch = false;
        settings.RaiseSettingsChanged();

        Assert.Equal(1, notifications.KillSwitchDisabledCalls);
        Assert.Equal(0, notifications.KillSwitchEnabledCalls);
        Assert.Equal(1, killSwitch.DisableCalls);
    }

    private static KillSwitchManager CreateManager(
        IKillSwitchService killSwitch,
        IUserSettingsService settings,
        RecordingNotificationService notifications)
    {
        return new KillSwitchManager(
            killSwitch,
            new TestVpnConnectionService(),
            new TestAuthenticationService(),
            settings,
            new NullLoggerService(),
            notifications);
    }

    private sealed class RecordingKillSwitchService : IKillSwitchService
    {
        public bool IsEnabled { get; private set; }
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }

        public Task EnableAsync(string? vpnServerIp = null, string? vpnLocalIp = null)
        {
            IsEnabled = true;
            EnableCalls++;
            return Task.CompletedTask;
        }

        public Task DisableAsync()
        {
            IsEnabled = false;
            DisableCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestVpnConnectionService : IVpnConnectionService
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
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            ErrorOccurred?.Invoke(this, string.Empty);
            StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
            StatsUpdated?.Invoke(this, new ConnectionStats(0, 0, 0, TimeSpan.Zero));
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthenticationService : IAuthenticationService
    {
        public event Action? SessionChanged;

        public bool IsAuthenticated => true;
        public string? UserEmail => "pro@example.test";
        public string? UserId => "user-1";
        public UserPlan Plan => UserPlan.Pro;

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

    private sealed class TestUserSettingsService : IUserSettingsService
    {
        public UserSettings Settings { get; } = new();
        public event EventHandler? SettingsChanged;

        public Task SaveSettingsAsync() => Task.CompletedTask;
        public Task LoadSettingsAsync() => Task.CompletedTask;
        public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class NullLoggerService : ILoggerService
    {
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public int KillSwitchEnabledCalls { get; private set; }
        public int KillSwitchDisabledCalls { get; private set; }

        public void NotifyVpnConnecting() { }
        public void NotifyVpnConnected(string serverName, string city, string country, string? ipAddress) { }
        public void NotifyVpnDisconnected() { }
        public void NotifyConnectionLost() { }
        public void NotifyConnectionError(string message) { }
        public void NotifyKillSwitchEnabled() => KillSwitchEnabledCalls++;
        public void NotifyKillSwitchDisabled() => KillSwitchDisabledCalls++;
        public void NotifyDataUsageWarning(double percentUsed) { }
        public void NotifyDataLimitReached() { }
    }
}
