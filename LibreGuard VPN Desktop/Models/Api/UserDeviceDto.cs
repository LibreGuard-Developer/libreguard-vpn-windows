using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

public sealed record UserDeviceDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("deviceIdHash")]
    public string? DeviceIdHash { get; init; }

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }

    [JsonPropertyName("deviceNickname")]
    public string? DeviceNickname { get; init; }

    [JsonPropertyName("lastSeenAt")]
    public DateTime LastSeenAt { get; init; }

    [JsonPropertyName("daysSinceLastSeen")]
    public int DaysSinceLastSeen { get; init; }
}
