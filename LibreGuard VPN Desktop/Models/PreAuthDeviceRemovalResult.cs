namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Result returned by a pre-authentication device removal request.
/// </summary>
public sealed record PreAuthDeviceRemovalResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? ErrorCode { get; init; }
    public int? RetryAfterSeconds { get; init; }

    public static PreAuthDeviceRemovalResult Ok(string? message = null) => new()
    {
        Success = true,
        Message = message
    };

    public static PreAuthDeviceRemovalResult Fail(
        string? message,
        string? errorCode = null,
        int? retryAfterSeconds = null) => new()
    {
        Message = message,
        ErrorCode = errorCode,
        RetryAfterSeconds = retryAfterSeconds
    };
}
