using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// JSON response from POST /api/login.
/// </summary>
internal sealed record LoginResponse
{
    [JsonPropertyName("requiresTwoFactor")]
    public bool RequiresTwoFactor { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("pendingLoginToken")]
    public string? PendingLoginToken { get; init; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("activeDevices")]
    public int ActiveDevices { get; init; }

    [JsonPropertyName("maxDevices")]
    public int MaxDevices { get; init; }

    [JsonPropertyName("planType")]
    public string? PlanType { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
