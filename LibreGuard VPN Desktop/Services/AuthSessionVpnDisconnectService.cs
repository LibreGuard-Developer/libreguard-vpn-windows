using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Ensures an authenticated VPN tunnel is dropped when the local auth session is lost.
/// </summary>
internal sealed class AuthSessionVpnDisconnectService : IDisposable
{
    private static readonly TimeSpan DefaultDisconnectTimeout = TimeSpan.FromSeconds(5);

    private readonly IAuthenticationService _authService;
    private readonly IVpnConnectionService _vpnConnectionService;
    private readonly ILoggerService _logger;
    private readonly TimeSpan _disconnectTimeout;
    private readonly SemaphoreSlim _disconnectLock = new(1, 1);
    private bool _wasAuthenticated;
    private bool _disposed;

    public AuthSessionVpnDisconnectService(
        IAuthenticationService authService,
        IVpnConnectionService vpnConnectionService,
        ILoggerService logger)
        : this(authService, vpnConnectionService, logger, DefaultDisconnectTimeout)
    {
    }

    internal AuthSessionVpnDisconnectService(
        IAuthenticationService authService,
        IVpnConnectionService vpnConnectionService,
        ILoggerService logger,
        TimeSpan disconnectTimeout)
    {
        ArgumentNullException.ThrowIfNull(authService);
        ArgumentNullException.ThrowIfNull(vpnConnectionService);
        ArgumentNullException.ThrowIfNull(logger);

        _authService = authService;
        _vpnConnectionService = vpnConnectionService;
        _logger = logger;
        _disconnectTimeout = disconnectTimeout;
        _wasAuthenticated = authService.IsAuthenticated;

        _authService.SessionChanged += OnSessionChanged;
    }

    private void OnSessionChanged()
    {
        if (_disposed)
            return;

        if (_authService.IsAuthenticated)
        {
            _wasAuthenticated = true;
            return;
        }

        if (!_wasAuthenticated)
            return;

        _wasAuthenticated = false;
        _ = DisconnectForSessionLossAsync();
    }

    private async Task DisconnectForSessionLossAsync()
    {
        if (!await _disconnectLock.WaitAsync(0))
            return;

        try
        {
            if (_authService.IsAuthenticated ||
                _vpnConnectionService.Status == ConnectionStatus.Disconnected)
            {
                return;
            }

            using var timeoutCts = new CancellationTokenSource(_disconnectTimeout);

            try
            {
                _logger.LogInformation("Disconnecting VPN because the auth session ended.");
                await _vpnConnectionService.DisconnectAsync(timeoutCts.Token);
                _logger.LogInformation("VPN disconnected after auth session ended.");
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning($"VPN disconnect after auth session loss timed out after {_disconnectTimeout.TotalSeconds:F0} seconds.");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to disconnect VPN after auth session ended.", ex);
            }
        }
        finally
        {
            _disconnectLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _authService.SessionChanged -= OnSessionChanged;
        _disconnectLock.Dispose();
    }
}
