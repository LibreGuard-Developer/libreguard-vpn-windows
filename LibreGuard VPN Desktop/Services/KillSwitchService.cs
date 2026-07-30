using System;
using System.Threading.Tasks;

namespace LibreGuard_VPN_Desktop.Services;

internal sealed class KillSwitchService : IKillSwitchService
{
    private readonly IVpnServiceClient _vpnServiceClient;
    private readonly IUserSettingsService _userSettingsService;

    public KillSwitchService(IVpnServiceClient vpnServiceClient, IUserSettingsService userSettingsService)
    {
        _vpnServiceClient = vpnServiceClient;
        _userSettingsService = userSettingsService;
    }

    public bool IsEnabled => _userSettingsService.Settings.KillSwitch;

    public async Task EnableAsync(string? vpnServerIp = null, string? vpnLocalIp = null)
    {
        var request = new VpnServiceRequest
        {
            Command = VpnCommandType.EnableKillSwitch,
            VpnServerIp = vpnServerIp,
            VpnLocalIp = vpnLocalIp
        };

        var response = await _vpnServiceClient.SendAsync(request);
        if (!response.Success)
        {
            throw new InvalidOperationException($"Failed to enable Kill Switch: {response.ErrorMessage}");
        }
    }

    public async Task DisableAsync()
    {
        var request = new VpnServiceRequest
        {
            Command = VpnCommandType.DisableKillSwitch
        };

        var response = await _vpnServiceClient.SendAsync(request);
        if (!response.Success)
        {
            throw new InvalidOperationException($"Failed to disable Kill Switch: {response.ErrorMessage}");
        }
    }
}
