namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Tracks which authentication screen is currently active.
/// </summary>
public enum AuthScreen
{
    Login,
    Register,
    EmailConfirmation,
    ForgotPassword,
    Authenticated
}
