using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// Generic error response returned by the management API.
/// </summary>
internal sealed record ApiErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("requiresVerification")]
    public bool RequiresVerification { get; init; }

    [JsonPropertyName("requiresDeviceRegistration")]
    public bool RequiresDeviceRegistration { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }

    [JsonPropertyName("enforcementEnabled")]
    public bool EnforcementEnabled { get; init; }

    [JsonPropertyName("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; init; }

    [JsonPropertyName("errors")]
    public List<string>? Errors { get; init; }

    [JsonPropertyName("devices")]
    public List<UserDeviceDto>? Devices { get; init; }

}
