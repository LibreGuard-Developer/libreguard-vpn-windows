using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// Represents the StrongSwan .sswan JSON configuration embedded in <see cref="VpnConfigResponse.ConfigContent"/>
/// for IKEv2 certificate-based VPN connections.
/// </summary>
internal sealed record StrongSwanConfig
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("remote")]
    public StrongSwanRemote Remote { get; init; } = new();

    [JsonPropertyName("local")]
    public StrongSwanLocal Local { get; init; } = new();

    [JsonPropertyName("dns-servers")]
    public string[] DnsServers { get; init; } = [];
}

/// <summary>
/// Remote (server) configuration for the IKEv2 connection.
/// </summary>
internal sealed record StrongSwanRemote
{
    [JsonPropertyName("addr")]
    public string Addr { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("cert")]
    public string Cert { get; init; } = string.Empty;

    [JsonPropertyName("ike")]
    public string Ike { get; init; } = string.Empty;

    [JsonPropertyName("esp")]
    public string Esp { get; init; } = string.Empty;
}

/// <summary>
/// Local (client) configuration containing the PKCS#12 certificate bundle.
/// </summary>
internal sealed record StrongSwanLocal
{
    /// <summary>
    /// Base64-encoded PKCS#12 (.p12/.pfx) bundle containing the client certificate,
    /// private key, and optionally the CA certificate chain.
    /// </summary>
    [JsonPropertyName("p12")]
    public string P12 { get; init; } = string.Empty;

    /// <summary>
    /// Password to decrypt the PKCS#12 bundle.
    /// </summary>
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}
