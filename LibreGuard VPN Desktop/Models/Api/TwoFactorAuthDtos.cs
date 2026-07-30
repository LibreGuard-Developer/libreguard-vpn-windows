using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// Response from 2FA setup endpoint containing authenticator URI and shared key.
/// </summary>
public sealed record TwoFactorSetupResponse
{
    public string? SharedKey { get; init; }
    public string? AuthenticatorUri { get; init; }
    public string? ManualEntryKey { get; init; }
}

/// <summary>
/// Request to enable 2FA with TOTP code.
/// </summary>
public sealed record TwoFactorEnableRequest
{
    public string? Code { get; init; }
}

/// <summary>
/// Response indicating 2FA has been successfully enabled.
/// </summary>
public sealed record TwoFactorEnableResponse
{
    public string? Message { get; init; }
    public string[]? RecoveryCodes { get; init; }
}

/// <summary>
/// Response containing newly generated recovery codes.
/// </summary>
public sealed record TwoFactorRecoveryCodesResponse
{
    public string? Message { get; init; }
    public string[]? RecoveryCodes { get; init; }
}

/// <summary>
/// Response for disabling 2FA.
/// </summary>
public sealed record TwoFactorDisableResponse
{
    public string? Message { get; init; }
}

/// <summary>
/// Response containing 2FA status for the current user.
/// </summary>
public sealed record TwoFactorStatusResponse
{
    [JsonPropertyName("is2faEnabled")]
    public bool IsEnabled { get; init; }
}


