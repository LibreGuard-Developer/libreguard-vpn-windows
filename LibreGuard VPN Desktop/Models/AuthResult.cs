namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Result of an authentication operation (login or register).
/// </summary>
public sealed record AuthResult
{
    public bool Success { get; init; }
    public bool RequiresTwoFactor { get; init; }
    public bool RequiresEmailConfirmation { get; init; }
    public bool DeviceLimitExceeded { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Email { get; init; }
    public string? UserId { get; init; }
    public string? PendingLoginToken { get; init; }
    public List<LibreGuard_VPN_Desktop.Models.Api.UserDeviceDto>? Devices { get; init; }

    public static AuthResult Ok() => new() { Success = true };

    public static AuthResult TwoFactorRequired(string email, string userId, string? pendingLoginToken = null) =>
        new() { RequiresTwoFactor = true, Email = email, UserId = userId, PendingLoginToken = pendingLoginToken };

    public static AuthResult EmailVerificationRequired(string? email) =>
        new() { RequiresEmailConfirmation = true, Email = email };

    public static AuthResult DeviceLimit(string? message, List<LibreGuard_VPN_Desktop.Models.Api.UserDeviceDto>? devices) =>
        new() { DeviceLimitExceeded = true, ErrorMessage = message, Devices = devices };

    public static AuthResult Fail(string message) =>
        new() { ErrorMessage = message };
}
