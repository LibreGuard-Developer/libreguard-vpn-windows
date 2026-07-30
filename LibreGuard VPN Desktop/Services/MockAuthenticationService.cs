using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Simulates authentication for UI development. Always succeeds after a short delay.
/// </summary>
internal sealed class MockAuthenticationService : IAuthenticationService
{
    private const string MockTwoFactorSecret = "MFRGGZDFMZTWQ2LK";

    public event Action? SessionChanged;

    public bool IsAuthenticated { get; private set; }
    public string? UserEmail { get; private set; }
    public string? UserId { get; private set; }
    public UserPlan Plan => UserPlan.Free;


    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1500, cancellationToken);
        IsAuthenticated = true;
        UserEmail = email;
        UserId = Guid.NewGuid().ToString();
        SessionChanged?.Invoke();
        return AuthResult.Ok();
    }

    public async Task<AuthResult> Verify2FaAsync(string email, string code, string? pendingLoginToken = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1000, cancellationToken);
        IsAuthenticated = true;
        UserEmail = email;
        return AuthResult.Ok();
    }

    public async Task<AuthResult> LoginWithOAuthAsync(string email, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1000, cancellationToken);
        return AuthResult.Fail("Legacy OAuth completion is disabled. Use Google sign-in instead.");
    }

    public async Task<AuthResult> LoginWithTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1000, cancellationToken);
        IsAuthenticated = true;
        UserEmail = "mock-token-user@example.com";
        UserId = Guid.NewGuid().ToString();
        SessionChanged?.Invoke();
        return AuthResult.Ok();
    }

    public async Task<AuthResult> LoginWithGoogleAsync(GoogleLoginContext loginContext, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1500, cancellationToken);
        IsAuthenticated = true;
        UserEmail = "mock-google-user@example.com";
        UserId = Guid.NewGuid().ToString();
        return AuthResult.Ok();
    }

    public async Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1500, cancellationToken);
        return new AuthResult { RequiresEmailConfirmation = true, Email = email, UserId = Guid.NewGuid().ToString() };
    }

    public async Task<bool> CheckEmailConfirmationAsync(string userId, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1000, cancellationToken);
        return true;
    }

    public Task ResendConfirmationAsync(string email, CancellationToken cancellationToken = default) =>
        Task.Delay(500, cancellationToken);

    public async Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthAsync(string email, string password, int deviceIdToRemove, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1000, cancellationToken);
        return PreAuthDeviceRemovalResult.Ok("Device removed successfully.");
    }

    public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthOAuthAsync(GoogleLoginContext loginContext, string provider, int deviceIdToRemove, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(PreAuthDeviceRemovalResult.Ok("Device removed successfully."));
    }

    public async Task<TwoFactorSetupResponse?> InitiateTwoFactorSetupAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(800, cancellationToken);
        return new TwoFactorSetupResponse
        {
            SharedKey = MockTwoFactorSecret,
            AuthenticatorUri = $"otpauth://totp/LibreGuard%20VPN:test@example.com?secret={MockTwoFactorSecret}&issuer=LibreGuard%20VPN&digits=6",
            ManualEntryKey = MockTwoFactorSecret
        };
    }

    public async Task<TwoFactorEnableResponse?> VerifyAndEnableTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1000, cancellationToken);
        if (code == "000000")
            return new TwoFactorEnableResponse { Message = "2FA enabled successfully.", RecoveryCodes = new[] { "CODE1", "CODE2", "CODE3", "CODE4", "CODE5" } };
        return null;
    }

    public async Task<TwoFactorRecoveryCodesResponse?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(800, cancellationToken);
        return new TwoFactorRecoveryCodesResponse
        {
            Message = "You have generated new recovery codes.",
            RecoveryCodes = new[] { "CODE1", "CODE2", "CODE3", "CODE4", "CODE5" }
        };
    }

    public async Task<TwoFactorDisableResponse?> DisableTwoFactorAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(800, cancellationToken);
        return new TwoFactorDisableResponse { Message = "2FA has been disabled." };
    }

    public Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<TwoFactorStatusResponse?>(new TwoFactorStatusResponse { IsEnabled = false });

    public Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) =>
        Task.Delay(1000, cancellationToken);

    public async Task<PasswordResetResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1000, cancellationToken);
        return PasswordResetResult.Ok("Password has been reset successfully.");
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default)

    {
        IsAuthenticated = false;
        UserEmail = null;
        UserId = null;
        SessionChanged?.Invoke();
        return Task.CompletedTask;
    }
}
