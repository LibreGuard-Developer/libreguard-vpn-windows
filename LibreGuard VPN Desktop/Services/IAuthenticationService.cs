using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Handles user authentication flows: login, registration, 2FA, and logout.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Authenticates with email and password. May return 2FA or email-verification results.
    /// </summary>
    Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a TOTP 2FA code after login indicated two-factor is required.
    /// </summary>
    Task<AuthResult> Verify2FaAsync(string email, string code, string? pendingLoginToken = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates using Google browser sign-in completion data.
    /// </summary>
    Task<AuthResult> LoginWithGoogleAsync(GoogleLoginContext loginContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Legacy email-based OAuth completion flow. Disabled by the backend hotfix.
    /// </summary>
    Task<AuthResult> LoginWithOAuthAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates using a JWT token directly (e.g. from deep link).
    /// </summary>
    Task<AuthResult> LoginWithTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new account with email and password.
    /// </summary>

    Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the user's email has been confirmed (polling).
    /// </summary>
    Task<bool> CheckEmailConfirmationAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resends the email confirmation link.
    /// </summary>
    Task ResendConfirmationAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a device pre-authentication when the device limit is reached.
    /// </summary>
    Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthAsync(string email, string password, int deviceIdToRemove, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a device pre-authentication using a Google authorization code when the device limit is reached.
    /// </summary>
    Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthOAuthAsync(GoogleLoginContext loginContext, string provider, int deviceIdToRemove, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates 2FA setup and returns the authenticator URI and shared key for authenticator apps.
    /// </summary>
    Task<TwoFactorSetupResponse?> InitiateTwoFactorSetupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the 2FA setup with a TOTP code and enables 2FA on the account.
    /// </summary>
    Task<TwoFactorEnableResponse?> VerifyAndEnableTwoFactorAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a fresh set of recovery codes for the authenticated user.
    /// </summary>
    Task<TwoFactorRecoveryCodesResponse?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables 2FA on the account.
    /// </summary>
    Task<TwoFactorDisableResponse?> DisableTwoFactorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates password reset for the specified email.
    /// </summary>
    Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the password using the email reset token.
    /// </summary>
    Task<PasswordResetResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the 2FA status for the current user.
    /// </summary>
    Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to deactivate the current device, then always clears the local session.
    /// </summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);


    /// <summary>
    /// Fired when the user's session state changes (e.g., logged out due to token expiration).
    /// </summary>
    event Action? SessionChanged;

    /// <summary>
    /// Whether the user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The authenticated user's email address.
    /// </summary>
    string? UserEmail { get; }

    /// <summary>
    /// The authenticated user's ID.
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// The authenticated user's subscription plan.
    /// </summary>
    UserPlan Plan { get; }
}
