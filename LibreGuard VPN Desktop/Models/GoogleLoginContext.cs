namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Google sign-in result returned from the browser flow.
/// Carries the authorization code and PKCE data needed by the management API.
/// </summary>
public sealed record GoogleLoginContext
{
    public string? ClientId { get; init; }
    public string? AuthorizationCode { get; init; }
    public string? RedirectUri { get; init; }
    public string? CodeVerifier { get; init; }
    public string? Email { get; init; }
    public string? ErrorMessage { get; init; }

    public bool HasCompletionData =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(AuthorizationCode) &&
        !string.IsNullOrWhiteSpace(RedirectUri) &&
        !string.IsNullOrWhiteSpace(CodeVerifier);
}
