using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Tests.ViewModels;

public sealed class LoginViewModelTests
{
    [Fact]
    public async Task GoogleLogin_SucceedsWithAuthorizationCode()
    {
        var auth = new FakeAuthenticationService();
        auth.GoogleLoginResults.Enqueue(AuthResult.Ok());
        var google = new FakeGoogleAuthService();
        google.Enqueue(Context("login-code"));
        var vm = CreateViewModel(auth, google);
        var loginSucceeded = false;
        vm.LoginSucceeded += (_, _) => loginSucceeded = true;

        await vm.LoginWithGoogleCommand.ExecuteAsync(null);

        Assert.True(loginSucceeded);
        Assert.Single(auth.GoogleLoginContexts);
        Assert.Equal("login-code", auth.GoogleLoginContexts[0].AuthorizationCode);
        Assert.False(vm.IsGoogleSignInRunning);
        Assert.False(vm.IsDeviceLimitReached);
    }

    [Fact]
    public async Task GoogleDeviceLimit_UsesFreshRemovalAndRetryCodes()
    {
        var auth = new FakeAuthenticationService();
        auth.GoogleLoginResults.Enqueue(DeviceLimit());
        auth.GoogleLoginResults.Enqueue(AuthResult.Ok());
        auth.GoogleRemovalResults.Enqueue(PreAuthDeviceRemovalResult.Ok("Device removed."));
        var google = new FakeGoogleAuthService();
        google.Enqueue(Context("initial-login-code"));
        google.Enqueue(Context("removal-code"));
        google.Enqueue(Context("retry-login-code"));
        var vm = CreateViewModel(auth, google);

        await vm.LoginWithGoogleCommand.ExecuteAsync(null);
        Assert.True(vm.IsDeviceLimitReached);
        vm.SelectedDeviceToRemove = vm.Devices![0];

        await vm.RemoveDeviceCommand.ExecuteAsync(null);

        Assert.Equal(2, auth.GoogleLoginContexts.Count);
        Assert.Equal("initial-login-code", auth.GoogleLoginContexts[0].AuthorizationCode);
        Assert.Equal("removal-code", auth.GoogleRemovalContexts.Single().AuthorizationCode);
        Assert.Equal("retry-login-code", auth.GoogleLoginContexts[1].AuthorizationCode);
        Assert.Equal(3, google.LoginCalls);
        Assert.False(vm.IsDeviceLimitReached);
        Assert.Null(vm.Devices);
    }

    [Fact]
    public async Task PasswordDeviceLimit_UsesPasswordRemovalThenRetriesPasswordLogin()
    {
        var auth = new FakeAuthenticationService();
        auth.PasswordLoginResults.Enqueue(DeviceLimit());
        auth.PasswordLoginResults.Enqueue(AuthResult.Ok());
        auth.PasswordRemovalResults.Enqueue(PreAuthDeviceRemovalResult.Ok());
        var vm = CreateViewModel(auth, new FakeGoogleAuthService());
        vm.Email = "user@example.test";
        vm.Password = "password";

        await vm.LoginCommand.ExecuteAsync(null);
        vm.SelectedDeviceToRemove = vm.Devices![0];
        await vm.RemoveDeviceCommand.ExecuteAsync(null);

        Assert.Equal(2, auth.PasswordLoginCalls);
        Assert.Equal(1, auth.PasswordRemovalCalls);
        Assert.Empty(auth.GoogleRemovalContexts);
        Assert.False(vm.IsDeviceLimitReached);
    }

    [Fact]
    public async Task GoogleTwoFactorDeviceLimit_RetainsGoogleRemovalMode()
    {
        var auth = new FakeAuthenticationService();
        auth.GoogleLoginResults.Enqueue(AuthResult.TwoFactorRequired("user@example.test", "user-1", "pending"));
        auth.GoogleLoginResults.Enqueue(AuthResult.Ok());
        auth.VerifyResults.Enqueue(DeviceLimit());
        auth.GoogleRemovalResults.Enqueue(PreAuthDeviceRemovalResult.Ok());
        var google = new FakeGoogleAuthService();
        google.Enqueue(Context("initial-code"));
        google.Enqueue(Context("removal-code"));
        google.Enqueue(Context("retry-code"));
        var vm = CreateViewModel(auth, google);

        await vm.LoginWithGoogleCommand.ExecuteAsync(null);
        vm.TwoFactorCode = "123456";
        await vm.VerifyTwoFactorCommand.ExecuteAsync(null);
        vm.SelectedDeviceToRemove = vm.Devices![0];
        await vm.RemoveDeviceCommand.ExecuteAsync(null);

        Assert.Single(auth.GoogleRemovalContexts);
        Assert.Equal(0, auth.PasswordRemovalCalls);
        Assert.Equal("removal-code", auth.GoogleRemovalContexts[0].AuthorizationCode);
        Assert.Equal("retry-code", auth.GoogleLoginContexts[^1].AuthorizationCode);
    }

    [Fact]
    public async Task GoogleRemovalFailure_KeepsDevicePickerAndShowsRetryDelay()
    {
        var auth = new FakeAuthenticationService();
        auth.GoogleLoginResults.Enqueue(DeviceLimit());
        auth.GoogleRemovalResults.Enqueue(PreAuthDeviceRemovalResult.Fail(
            "Too many requests.",
            "RATE_LIMIT_EXCEEDED",
            30));
        var google = new FakeGoogleAuthService();
        google.Enqueue(Context("initial-code"));
        google.Enqueue(Context("removal-code"));
        var vm = CreateViewModel(auth, google);

        await vm.LoginWithGoogleCommand.ExecuteAsync(null);
        vm.SelectedDeviceToRemove = vm.Devices![0];
        await vm.RemoveDeviceCommand.ExecuteAsync(null);

        Assert.True(vm.IsDeviceLimitReached);
        Assert.NotNull(vm.SelectedDeviceToRemove);
        Assert.Contains("30 seconds", vm.ErrorMessage);
        Assert.Equal(2, google.LoginCalls);
    }

    [Fact]
    public async Task CancelGoogleLogin_CancelsActiveBrowserFlowAndCleansState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var google = new FakeGoogleAuthService();
        google.Enqueue(async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Context("unreachable");
        });
        var vm = CreateViewModel(new FakeAuthenticationService(), google);

        var login = vm.LoginWithGoogleCommand.ExecuteAsync(null);
        await started.Task;
        vm.CancelGoogleLoginCommand.Execute(null);
        await login;

        Assert.False(vm.IsGoogleSignInRunning);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsDeviceLimitReached);
        Assert.Equal("Sign-in cancelled.", vm.ErrorMessage);
    }

    private static LoginViewModel CreateViewModel(FakeAuthenticationService auth, FakeGoogleAuthService google) =>
        new(auth, new RecordingLoggerService(), google);

    private static AuthResult DeviceLimit() => AuthResult.DeviceLimit(
        "Device limit reached.",
        [new UserDeviceDto { Id = 42, DeviceNickname = "Existing device" }]);

    private static GoogleLoginContext Context(string code) => new()
    {
        ClientId = "client.apps.googleusercontent.com",
        AuthorizationCode = code,
        RedirectUri = "http://localhost:54321/",
        CodeVerifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"
    };

    private sealed class FakeGoogleAuthService : IGoogleAuthService
    {
        private readonly Queue<Func<CancellationToken, Task<GoogleLoginContext>>> _logins = new();

        public int LoginCalls { get; private set; }

        public void Enqueue(GoogleLoginContext context) =>
            Enqueue(_ => Task.FromResult(context));

        public void Enqueue(Func<CancellationToken, Task<GoogleLoginContext>> login) =>
            _logins.Enqueue(login);

        public Task<GoogleLoginContext> LoginAsync(CancellationToken cancellationToken = default)
        {
            LoginCalls++;
            return _logins.Dequeue()(cancellationToken);
        }

        public Task LogoutAsync() => Task.CompletedTask;
    }

    private sealed class FakeAuthenticationService : IAuthenticationService
    {
        public Queue<AuthResult> PasswordLoginResults { get; } = new();
        public Queue<AuthResult> GoogleLoginResults { get; } = new();
        public Queue<AuthResult> VerifyResults { get; } = new();
        public Queue<PreAuthDeviceRemovalResult> PasswordRemovalResults { get; } = new();
        public Queue<PreAuthDeviceRemovalResult> GoogleRemovalResults { get; } = new();
        public List<GoogleLoginContext> GoogleLoginContexts { get; } = new();
        public List<GoogleLoginContext> GoogleRemovalContexts { get; } = new();
        public int PasswordLoginCalls { get; private set; }
        public int PasswordRemovalCalls { get; private set; }

        public event Action? SessionChanged
        {
            add { }
            remove { }
        }
        public bool IsAuthenticated { get; private set; }
        public string? UserEmail { get; private set; }
        public string? UserId { get; private set; }
        public UserPlan Plan => UserPlan.Free;

        public Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            PasswordLoginCalls++;
            return Task.FromResult(PasswordLoginResults.Dequeue());
        }

        public Task<AuthResult> Verify2FaAsync(string email, string code, string? pendingLoginToken = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(VerifyResults.Dequeue());

        public Task<AuthResult> LoginWithGoogleAsync(GoogleLoginContext loginContext, CancellationToken cancellationToken = default)
        {
            GoogleLoginContexts.Add(loginContext);
            return Task.FromResult(GoogleLoginResults.Dequeue());
        }

        public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthAsync(string email, string password, int deviceIdToRemove, CancellationToken cancellationToken = default)
        {
            PasswordRemovalCalls++;
            return Task.FromResult(PasswordRemovalResults.Dequeue());
        }

        public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthOAuthAsync(GoogleLoginContext loginContext, string provider, int deviceIdToRemove, CancellationToken cancellationToken = default)
        {
            GoogleRemovalContexts.Add(loginContext);
            return Task.FromResult(GoogleRemovalResults.Dequeue());
        }

        public Task<AuthResult> LoginWithOAuthAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthResult> LoginWithTokenAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CheckEmailConfirmationAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResendConfirmationAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TwoFactorSetupResponse?> InitiateTwoFactorSetupAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TwoFactorEnableResponse?> VerifyAndEnableTwoFactorAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TwoFactorRecoveryCodesResponse?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TwoFactorDisableResponse?> DisableTwoFactorAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PasswordResetResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingLoggerService : ILoggerService
    {
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }
}
