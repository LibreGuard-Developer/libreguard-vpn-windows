using System;
using System.Threading.Tasks;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Coordinates the Kill Switch state based on VPN connection status, user settings, and subscription plan.
/// </summary>
public sealed class KillSwitchManager : IDisposable
{
    private readonly IKillSwitchService _killSwitchService;
    private readonly IVpnConnectionService _vpnConnectionService;
    private readonly IAuthenticationService _authService;
    private readonly IUserSettingsService _userSettingsService;
    private readonly ILoggerService _logger;
    private readonly INotificationService _notificationService;

    private string? _currentVpnServerIp;
    private string? _currentVpnLocalIp;
    private bool? _lastKillSwitchState;

    public KillSwitchManager(
        IKillSwitchService killSwitchService,
        IVpnConnectionService vpnConnectionService,
        IAuthenticationService authService,
        IUserSettingsService userSettingsService,
        ILoggerService logger,
        INotificationService notificationService)
    {
        _killSwitchService = killSwitchService;
        _vpnConnectionService = vpnConnectionService;
        _authService = authService;
        _userSettingsService = userSettingsService;
        _logger = logger;
        _notificationService = notificationService;

        _vpnConnectionService.StatusChanged += OnVpnStatusChanged;
        _authService.SessionChanged += OnSessionChanged;
        _userSettingsService.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync()
    {
        await EvaluateStateAsync(notify: false);
    }

    private async void OnVpnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status == ConnectionStatus.Connected)
        {
            // We might need to extract the VPN Server IP and Local IP from the connection service.
            // For now, we assume the connection service provides the VpnIpAddress.
            _currentVpnLocalIp = _vpnConnectionService.VpnIpAddress;
            // The server IP should be known before connecting, but we can update it here if needed.
        }
        else if (status == ConnectionStatus.Disconnected)
        {
            _currentVpnLocalIp = null;
        }

        await EvaluateStateAsync(notify: true);
    }

    private async void OnSessionChanged()
    {
        await EvaluateStateAsync(notify: true);
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        await EvaluateStateAsync(notify: true);
    }

    /// <summary>
    /// Sets the target VPN server IP before connecting, so the Kill Switch can allow it.
    /// </summary>
    public async Task SetTargetServerIpAsync(string serverHostnameOrIp)
    {
        try
        {
            if (System.Net.IPAddress.TryParse(serverHostnameOrIp, out _))
            {
                _currentVpnServerIp = serverHostnameOrIp;
            }
            else
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(serverHostnameOrIp);
                if (addresses.Length > 0)
                {
                    _currentVpnServerIp = addresses[0].ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to resolve VPN server IP for {serverHostnameOrIp}", ex);
        }

        await EvaluateStateAsync(notify: true);
    }

    private async Task EvaluateStateAsync(bool notify)
    {
        try
        {
            bool isPro = _authService.Plan == UserPlan.Pro;
            bool isKillSwitchEnabled = _userSettingsService.Settings.KillSwitch;

            if (!isPro || !isKillSwitchEnabled)
            {
                await _killSwitchService.DisableAsync();
                UpdateNotificationState(enabled: false, notify);
                return;
            }

            // If Pro and Kill Switch is enabled, we engage it.
            // We pass the current VPN Server IP and Local IP to allow them through the firewall.
            await _killSwitchService.EnableAsync(_currentVpnServerIp, _currentVpnLocalIp);
            UpdateNotificationState(enabled: true, notify);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to evaluate Kill Switch state.", ex);
        }
    }

    private void UpdateNotificationState(bool enabled, bool notify)
    {
        if (_lastKillSwitchState == enabled)
            return;

        _lastKillSwitchState = enabled;

        if (!notify)
            return;

        if (enabled)
            _notificationService.NotifyKillSwitchEnabled();
        else
            _notificationService.NotifyKillSwitchDisabled();
    }

    public void Dispose()
    {
        _vpnConnectionService.StatusChanged -= OnVpnStatusChanged;
        _authService.SessionChanged -= OnSessionChanged;
        _userSettingsService.SettingsChanged -= OnSettingsChanged;
    }
}
