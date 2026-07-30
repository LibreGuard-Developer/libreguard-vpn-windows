namespace LibreGuard_VPN_Desktop.Services;

public interface IVpnServiceLifecycleService
{
    Task EnsureServiceRunningAsync(CancellationToken cancellationToken = default);
}
