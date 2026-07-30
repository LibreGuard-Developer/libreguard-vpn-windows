using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// Server-authoritative, account-wide DNS filtering preference.
/// </summary>
public sealed record DnsPreferenceResponse
{
    [JsonPropertyName("requestedEnabled")]
    public bool RequestedEnabled { get; init; }

    [JsonPropertyName("canUseAdBlocking")]
    public bool CanUseAdBlocking { get; init; }

    [JsonPropertyName("effectiveEnabled")]
    public bool EffectiveEnabled { get; init; }

    [JsonPropertyName("effectiveMode")]
    public string EffectiveMode { get; init; } = string.Empty;

    [JsonPropertyName("propagationSeconds")]
    public int PropagationSeconds { get; init; }
}

internal sealed record DnsPreferenceUpdateRequest
{
    [JsonPropertyName("adBlockingEnabled")]
    public bool AdBlockingEnabled { get; init; }
}

internal sealed record DnsPreferenceErrorResponse
{
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("settings")]
    public DnsPreferenceResponse? Settings { get; init; }
}
