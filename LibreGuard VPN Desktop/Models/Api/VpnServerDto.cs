using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// A single VPN server entry from GET /api/vpn/servers.
/// </summary>
internal sealed record VpnServerDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("serverName")]
    public string ServerName { get; init; } = string.Empty;

    [JsonPropertyName("serverIp")]
    public string ServerIp { get; init; } = string.Empty;

    [JsonPropertyName("serverHostname")]
    public string? ServerHostname { get; init; }

    [JsonPropertyName("country")]
    public string Country { get; init; } = string.Empty;

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("linkSpeed")]
    public int LinkSpeed { get; init; }

    [JsonPropertyName("pricingTier")]
    public string PricingTier { get; init; } = string.Empty;

    [JsonPropertyName("load")]
    public double? Load { get; init; }

    [JsonPropertyName("latencyPingPort")]
    public int LatencyPingPort { get; init; }

    [JsonPropertyName("loadDataFresh")]
    public bool LoadDataFresh { get; init; }
}

/// <summary>
/// Wrapper for GET /api/vpn/servers response.
/// </summary>
internal sealed record VpnServerListResponse
{
    [JsonPropertyName("servers")]
    public List<VpnServerDto> Servers { get; init; } = [];
}
