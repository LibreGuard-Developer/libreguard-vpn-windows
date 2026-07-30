using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class AuthSessionVpnDisconnectServiceTests
{
    [Fact]
    public async Task SessionChanged_WhenAuthenticatedSessionEnds_DisconnectsConnectedVpn()
    {
        var auth = new ControllableAuthenticationService(isAuthenticated: true);
        var vpn = new RecordingVpnConnectionService(ConnectionStatus.Connected);
        var logger = new RecordingLoggerService();
        using var service = new AuthSessionVpnDisconnectService(auth, vpn, logger, TimeSpan.FromSeconds(1));

        auth.SetAuthenticated(false);

        await vpn.WaitForDisconnectAsync();

        Assert.Equal(1, vpn.DisconnectCalls);
        Assert.Contains(logger.InformationMessages, m => m.Contains("auth session ended", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SessionChanged_WhenStillAuthenticated_DoesNotDisconnect()
    {
        var auth = new ControllableAuthenticationService(isAuthenticated: true);
        var vpn = new RecordingVpnConnectionService(ConnectionStatus.Connected);
        using var service = new AuthSessionVpnDisconnectService(auth, vpn, new RecordingLoggerService(), TimeSpan.FromMilliseconds(50));

        auth.RaiseSessionChanged();

        await Task.Delay(100);

        Assert.Equal(0, vpn.DisconnectCalls);
    }

    [Fact]
    public async Task SessionChanged_WhenVpnAlreadyDisconnected_DoesNotDisconnect()
    {
        var auth = new ControllableAuthenticationService(isAuthenticated: true);
        var vpn = new RecordingVpnConnectionService(ConnectionStatus.Disconnected);
        using var service = new AuthSessionVpnDisconnectService(auth, vpn, new RecordingLoggerService(), TimeSpan.FromMilliseconds(50));

        auth.SetAuthenticated(false);

        await Task.Delay(100);

        Assert.Equal(0, vpn.DisconnectCalls);
    }

    [Fact]
    public async Task SessionChanged_WhenRepeatedDuringDisconnect_DisconnectsOnce()
    {
        var auth = new ControllableAuthenticationService(isAuthenticated: true);
        var vpn = new RecordingVpnConnectionService(ConnectionStatus.Connected)
        {
            HoldDisconnectUntilCanceled = true
        };
        using var service = new AuthSessionVpnDisconnectService(auth, vpn, new RecordingLoggerService(), TimeSpan.FromMilliseconds(50));

        auth.SetAuthenticated(false);
        auth.RaiseSessionChanged();
        auth.RaiseSessionChanged();

        await vpn.WaitForDisconnectAsync();
        await Task.Delay(100);

        Assert.Equal(1, vpn.DisconnectCalls);
    }

    [Fact]
    public async Task SessionChanged_WhenDisconnectThrows_LogsAndDoesNotRethrow()
    {
        var auth = new ControllableAuthenticationService(isAuthenticated: true);
        var vpn = new RecordingVpnConnectionService(ConnectionStatus.Connected)
        {
            DisconnectException = new InvalidOperationException("simulated disconnect failure")
        };
        var logger = new RecordingLoggerService();
        using var service = new AuthSessionVpnDisconnectService(auth, vpn, logger, TimeSpan.FromSeconds(1));

        auth.SetAuthenticated(false);

        await vpn.WaitForDisconnectAsync();
        await Task.Delay(50);

        Assert.Equal(1, vpn.DisconnectCalls);
        Assert.Single(logger.Errors);
        Assert.Contains("simulated disconnect failure", logger.Errors[0].Exception?.Message);
    }

    [Fact]
    public async Task SessionChanged_WhenDisconnectTimesOut_LogsWarning()
    {
        var auth = new ControllableAuthenticationService(isAuthenticated: true);
        var vpn = new RecordingVpnConnectionService(ConnectionStatus.Connected)
        {
            HoldDisconnectUntilCanceled = true
        };
        var logger = new RecordingLoggerService();
        using var service = new AuthSessionVpnDisconnectService(auth, vpn, logger, TimeSpan.FromMilliseconds(20));

        auth.SetAuthenticated(false);

        await vpn.WaitForDisconnectAsync();
        await WaitForAsync(() => logger.WarningMessages.Count > 0);

        Assert.Equal(1, vpn.DisconnectCalls);
        Assert.Contains(logger.WarningMessages, m => m.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }
    }

    private sealed class ControllableAuthenticationService : IAuthenticationService
    {
        public ControllableAuthenticationService(bool isAuthenticated)
        {
            IsAuthenticated = isAuthenticated;
        }

        public event Action? SessionChanged;

        public bool IsAuthenticated { get; private set; }
        public string? UserEmail => IsAuthenticated ? "user@example.test" : null;
        public string? UserId => IsAuthenticated ? "user-1" : null;
        public UserPlan Plan => UserPlan.Free;

        public void SetAuthenticated(bool value)
        {
            IsAuthenticated = value;
            RaiseSessionChanged();
        }

        public void RaiseSessionChanged() => SessionChanged?.Invoke();

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
            SetAuthenticated(false);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingVpnConnectionService : IVpnConnectionService
    {
        private readonly TaskCompletionSource _disconnectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingVpnConnectionService(ConnectionStatus status)
        {
            Status = status;
        }

        public int DisconnectCalls { get; private set; }
        public bool HoldDisconnectUntilCanceled { get; init; }
        public Exception? DisconnectException { get; init; }
        public ConnectionStatus Status { get; private set; }
        public ConnectionStats? CurrentStats => null;
        public string? VpnIpAddress => null;
        public string? LastErrorMessage => null;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<ConnectionStats>? StatsUpdated;

        public Task WaitForDisconnectAsync() => _disconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            _disconnectStarted.TrySetResult();

            if (DisconnectException is not null)
                throw DisconnectException;

            if (HoldDisconnectUntilCanceled)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            Status = ConnectionStatus.Disconnected;
            StatusChanged?.Invoke(this, Status);
        }

        public void RaiseUnusedEvents()
        {
            ErrorOccurred?.Invoke(this, string.Empty);
            StatsUpdated?.Invoke(this, new ConnectionStats(0, 0, 0, TimeSpan.Zero));
        }
    }

    private sealed class RecordingLoggerService : ILoggerService
    {
        public List<string> InformationMessages { get; } = [];
        public List<string> WarningMessages { get; } = [];
        public List<(string Message, Exception? Exception)> Errors { get; } = [];

        public void LogInformation(string message) => InformationMessages.Add(message);
        public void LogWarning(string message) => WarningMessages.Add(message);
        public void LogError(string message, Exception? ex = null) => Errors.Add((message, ex));
    }
}
