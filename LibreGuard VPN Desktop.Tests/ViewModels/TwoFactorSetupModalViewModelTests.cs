using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Tests.ViewModels;

public sealed class TwoFactorSetupModalViewModelTests
{
    [Fact]
    public async Task VerifyAndEnableCommand_WhenRecoveryCodesAreReturned_ShowsThemUntilDismissed()
    {
        var authService = new RecordingAuthenticationService
        {
            EnableResponse = new TwoFactorEnableResponse
            {
                Message = "Your authenticator app has been verified.",
                RecoveryCodes = new[] { "code-1", "code-2", "code-3" }
            }
        };
        var viewModel = new TwoFactorSetupModalViewModel(authService)
        {
            VerificationCode = "123456"
        };

        var completedCount = 0;
        viewModel.SetupCompleted += (_, _) => completedCount++;

        await viewModel.VerifyAndEnableCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowBackupCodes);
        Assert.Equal(string.Join(Environment.NewLine, new[] { "code-1", "code-2", "code-3" }), viewModel.BackupCodes);
        Assert.Equal("Your authenticator app has been verified.", viewModel.SuccessMessage);
        Assert.Equal(0, completedCount);

        viewModel.CancelCommand.Execute(null);

        Assert.Equal(1, completedCount);
    }

    [Fact]
    public async Task VerifyAndEnableCommand_WhenEnableResponseOmitsRecoveryCodes_FallsBackToGenerateEndpoint()
    {
        var authService = new RecordingAuthenticationService
        {
            EnableResponse = new TwoFactorEnableResponse
            {
                Message = "Enabled, but no codes were returned."
            },
            GeneratedRecoveryCodesResponse = new TwoFactorRecoveryCodesResponse
            {
                Message = "You have generated new recovery codes.",
                RecoveryCodes = new[] { "code-a", "code-b" }
            }
        };
        var viewModel = new TwoFactorSetupModalViewModel(authService)
        {
            VerificationCode = "123456"
        };

        await viewModel.VerifyAndEnableCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowBackupCodes);
        Assert.Equal(new[] { "code-a", "code-b" }, viewModel.RecoveryCodeItems);
        Assert.Equal(string.Join(Environment.NewLine, new[] { "code-a", "code-b" }), viewModel.BackupCodes);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task GenerateRecoveryCodesCommand_WhenClicked_PopulatesTheRecoveryCodeList()
    {
        var authService = new RecordingAuthenticationService
        {
            GeneratedRecoveryCodesResponse = new TwoFactorRecoveryCodesResponse
            {
                Message = "You have generated new recovery codes.",
                RecoveryCodes = new[] { "code-x", "code-y" }
            }
        };
        var viewModel = new TwoFactorSetupModalViewModel(authService);

        await viewModel.GenerateRecoveryCodesCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowBackupCodes);
        Assert.True(viewModel.HasRecoveryCodes);
        Assert.Equal(new[] { "code-x", "code-y" }, viewModel.RecoveryCodeItems);
        Assert.Equal(string.Join(Environment.NewLine, new[] { "code-x", "code-y" }), viewModel.BackupCodes);
        Assert.Equal("You have generated new recovery codes.", viewModel.RecoveryCodesMessage);
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public TwoFactorEnableResponse? EnableResponse { get; init; }
        public TwoFactorSetupResponse? SetupResponse { get; init; }
        public TwoFactorRecoveryCodesResponse? GeneratedRecoveryCodesResponse { get; init; }

        public event Action? SessionChanged;

        public bool IsAuthenticated => false;
        public string? UserEmail => null;
        public string? UserId => null;
        public UserPlan Plan => UserPlan.Free;

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
        public Task<TwoFactorSetupResponse?> InitiateTwoFactorSetupAsync(CancellationToken cancellationToken = default) => Task.FromResult(SetupResponse);
        public Task<TwoFactorEnableResponse?> VerifyAndEnableTwoFactorAsync(string code, CancellationToken cancellationToken = default) => Task.FromResult(EnableResponse);
        public Task<TwoFactorRecoveryCodesResponse?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default) => Task.FromResult(GeneratedRecoveryCodesResponse);
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
}
