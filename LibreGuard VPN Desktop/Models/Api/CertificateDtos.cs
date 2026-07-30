using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

internal sealed class CertificateRequest
{
    [JsonPropertyName("serverId")]
    public int ServerId { get; set; }

    [JsonPropertyName("vpnType")]
    public string VpnType { get; set; } = string.Empty;
}

internal sealed class CertificateRequestResponse
{
    [JsonPropertyName("jobId")]
    public int JobId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

internal sealed class CertificateJobResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("outputCertificateId")]
    public int? OutputCertificateId { get; set; }
}
