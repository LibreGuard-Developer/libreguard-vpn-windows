namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Result of a password reset operation.
/// </summary>
public sealed record PasswordResetResult
{
    public bool Success { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string>? Errors { get; init; }

    public static PasswordResetResult Ok(string? message) => new()
    {
        Success = true,
        Message = message
    };

    public static PasswordResetResult Fail(string? message, IReadOnlyList<string>? errors = null) => new()
    {
        Message = message,
        Errors = errors
    };
}
