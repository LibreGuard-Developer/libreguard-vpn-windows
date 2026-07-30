using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Provides the account-wide, server-authoritative DNS filtering preference.
/// </summary>
public interface IDnsSettingsService
{
    Task<DnsPreferenceResponse?> GetPreferenceAsync(CancellationToken ct = default);

    Task<DnsPreferenceUpdateResult> SetAdBlockingAsync(bool enabled, CancellationToken ct = default);
}

public sealed record DnsPreferenceUpdateResult
{
    public bool Success { get; init; }

    public DnsPreferenceResponse? Preference { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }
}
