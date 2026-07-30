using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// JSON response from POST /api/register.
/// </summary>
internal sealed record RegisterResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("accountStatus")]
    public string? AccountStatus { get; init; }

    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("requiresEmailConfirmation")]
    public bool RequiresEmailConfirmation { get; init; }
}

/// <summary>
/// JSON response from GET /api/register/check-confirmation/{userId}.
/// </summary>
internal sealed record EmailConfirmationStatusResponse
{
    [JsonPropertyName("emailConfirmed")]
    public bool EmailConfirmed { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("userId")]
    public string? UserId { get; init; }
}
