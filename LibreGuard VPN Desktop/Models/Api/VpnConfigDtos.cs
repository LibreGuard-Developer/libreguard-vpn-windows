using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// Request body for POST /api/vpn/config.
/// </summary>
internal sealed record VpnConfigRequest
{
    [JsonPropertyName("serverId")]
    public int ServerId { get; init; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;
}

/// <summary>
/// Response from POST /api/vpn/config containing VPN configuration and credentials.
/// </summary>
internal sealed record VpnConfigResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    [JsonPropertyName("serverName")]
    public string ServerName { get; init; } = string.Empty;

    [JsonPropertyName("serverIp")]
    public string ServerIp { get; init; } = string.Empty;

    [JsonPropertyName("certificateName")]
    public string CertificateName { get; init; } = string.Empty;

    [JsonPropertyName("configContent")]
    public string ConfigContent { get; init; } = string.Empty;

    [JsonPropertyName("passphrase")]
    public string? Passphrase { get; init; }

    [JsonPropertyName("encryptedPassphrase")]
    public EncryptedPassphrasePayload? EncryptedPassphrase { get; init; }

    [JsonPropertyName("issueDate")]
    public DateTime IssueDate { get; init; }

    [JsonPropertyName("expirationDate")]
    public DateTime ExpirationDate { get; init; }

    [JsonPropertyName("clientIp")]
    public string ClientIp { get; init; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;
}

/// <summary>
/// Device-bound encrypted passphrase returned by the management API.
/// </summary>
internal sealed record EncryptedPassphrasePayload
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; init; } = string.Empty;

    [JsonPropertyName("keyId")]
    public string KeyId { get; init; } = string.Empty;

    [JsonPropertyName("ciphertext")]
    public string Ciphertext { get; init; } = string.Empty;
}
