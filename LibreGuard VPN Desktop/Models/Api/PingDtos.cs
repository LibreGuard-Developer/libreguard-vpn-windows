using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// Response from GET /ping endpoint on VPN servers.
/// Used for latency measurement.
/// </summary>
internal sealed record PingResponse
{
    [JsonPropertyName("pong")]
    public bool Pong { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }
}
