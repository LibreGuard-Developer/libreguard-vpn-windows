using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

public interface IGoogleAuthService
{
    Task<GoogleLoginContext> LoginAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync();
}
