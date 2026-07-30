using System.Globalization;
using System.IO;
using System.Timers;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Real VPN connection service that retrieves configuration from the management API,
/// stores credentials securely via DPAPI, and establishes tunnels using protocol-specific strategies.
/// Replaces MockVpnConnectionService in the DI container.
/// </summary>
internal sealed class WinVpnConnectionService : IVpnConnectionService, IDisposable
{
    private readonly IVpnConfigService _configService;
    private readonly VpnConfigStorageService _configStorage;
    private readonly OpenVpnTunnelStrategy _openVpnStrategy;
    private readonly IKEv2TunnelStrategy _ikev2Strategy;
    private readonly IStatisticsService _statisticsService;
    private readonly ILoggerService _logger;
    private readonly IVpnServiceClient _vpnServiceClient;
    private readonly IOpenVpnDependencyService? _openVpnDependencyService;
    private readonly OpenVpnSettings _openVpnSettings;
    private readonly System.Timers.Timer _statsTimer;

    private IVpnTunnelStrategy? _activeStrategy;
    private string? _activeCertificateName;
    private string? _activeConfigPath;
    private string? _activePassphrase;
    private string? _activeServerIp;
    private VpnProtocol _activeProtocol;
    private DateTime _connectedSince;
    private ServerLocation? _currentServer;
    private CancellationTokenSource? _reconnectCts;
    private volatile bool _intentionalDisconnect;

    public WinVpnConnectionService(
        IVpnConfigService configService,
        VpnConfigStorageService configStorage,
        CertificateCacheService certCache,
        IVpnServiceClient vpnServiceClient,
        IStatisticsService statisticsService,
        ILoggerService logger,
        IOpenVpnDependencyService? openVpnDependencyService = null)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(configStorage);
        ArgumentNullException.ThrowIfNull(certCache);
        ArgumentNullException.ThrowIfNull(vpnServiceClient);
        ArgumentNullException.ThrowIfNull(statisticsService);
        ArgumentNullException.ThrowIfNull(logger);

        _configService = configService;
        _configStorage = configStorage;
        _statisticsService = statisticsService;
        _logger = logger;
        _vpnServiceClient = vpnServiceClient;
        _openVpnDependencyService = openVpnDependencyService;
        _openVpnSettings = OpenVpnSettings.Load();
        _openVpnStrategy = new OpenVpnTunnelStrategy(vpnServiceClient);
        _ikev2Strategy = new IKEv2TunnelStrategy(certCache, vpnServiceClient);

        _openVpnStrategy.ConnectionStateChanged += OnOpenVpnStateChanged;

        _statsTimer = new System.Timers.Timer(1000);
        _statsTimer.Elapsed += OnStatsTimerElapsed;
    }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
    public ConnectionStats? CurrentStats { get; private set; }
    public string? VpnIpAddress { get; private set; }
    public string? LastErrorMessage { get; private set; }

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<ConnectionStats>? StatsUpdated;
    public event EventHandler<string>? ErrorOccurred;

    public async Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (Status != ConnectionStatus.Disconnected)
            await DisconnectAsync(cancellationToken);

        // Cancel any in-flight reconnect loop
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
        _intentionalDisconnect = false;

        _currentServer = server;
        _activeProtocol = protocol;
        LastErrorMessage = null;
        SetStatus(ConnectionStatus.Connecting);

        try
        {
            // 1. Map protocol enum to API string
            var protocolString = protocol switch
            {
                VpnProtocol.IKEv2 => "IKEV2",
                VpnProtocol.OpenVPN => "OpenVPN",
                _ => throw new ArgumentOutOfRangeException(nameof(protocol))
            };

            if (protocol == VpnProtocol.OpenVPN && _openVpnDependencyService is not null)
                await _openVpnDependencyService.EnsureReadyAsync(protocol, cancellationToken);

            // 2. Parse server ID (ServerLocation.Id is string, API expects int)
            if (!int.TryParse(server.Id, CultureInfo.InvariantCulture, out var serverId))
                throw new InvalidOperationException($"Invalid server ID format: {server.Id}");

            // 3. Request configuration from the management API
            _logger.LogInformation($"Requesting VPN config: serverId={serverId}, protocol={protocolString}");
            var config = await _configService.GetConfigAsync(serverId, protocolString, cancellationToken);
            
            if (config is null)
                throw new InvalidOperationException("Failed to retrieve VPN configuration from the server. Response was null.");

            if (!config.Success)
                throw new InvalidOperationException("Failed to retrieve VPN configuration from the server. Success flag is false.");

            // 4. Store config securely on disk (DPAPI-encrypted passphrase)
            var configPath = _configStorage.SaveConfig(config);
            _activeCertificateName = config.CertificateName;
            _activeConfigPath = configPath;

            // 5. Determine the server IP to connect to
            var serverIp = config.ServerIp;
            if (string.IsNullOrEmpty(serverIp))
                serverIp = server.ServerIp ?? server.ServerHostname
                    ?? throw new InvalidOperationException("No server IP available for connection.");

            _activeServerIp = serverIp;

            // 6. Retrieve the passphrase (decrypted from DPAPI)
            var passphrase = _configStorage.LoadPassphrase(config.CertificateName);
            _activePassphrase = passphrase;

            // 7. Select and execute the appropriate tunnel strategy
            var strategy = SelectStrategy(protocol);
            _activeStrategy = strategy;

            await strategy.ConnectAsync(configPath, passphrase, serverIp, cancellationToken);

            // 8. Connection established
            _connectedSince = DateTime.UtcNow;
            _lastBytesIn = 0;
            _lastBytesOut = 0;
            _lastStatsTime = DateTime.UtcNow;
            VpnIpAddress = _activeServerIp;
            SetStatus(ConnectionStatus.Connected);
            _statsTimer.Start();

            _logger.LogInformation($"VPN connected via {protocolString} to {config.ServerName}");
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(CancellationToken.None);
            SetStatus(ConnectionStatus.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"VPN connection failed: {ex.Message}");
            await CleanupAsync(CancellationToken.None);
            SetError(ClassifyConnectionError(ex));
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // Cancel any in-flight reconnect loop
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;

        _statsTimer.Stop();

        if (Status == ConnectionStatus.Connected && _currentServer != null)
        {
            var end = DateTime.UtcNow;
            var downloadMb = (_lastBytesIn) / (1024.0 * 1024.0);
            var uploadMb = (_lastBytesOut) / (1024.0 * 1024.0);

            var record = new VpnSessionRecord(
                StartTime: _connectedSince,
                EndTime: end,
                ServerName: _currentServer.ServerName,
                DownloadMb: downloadMb,
                UploadMb: uploadMb);

            await _statisticsService.RecordSessionAsync(record);
        }

        _intentionalDisconnect = true;
        if (Status != ConnectionStatus.Disconnected)
            SetStatus(ConnectionStatus.Disconnecting);

        try
        {
            if (_activeStrategy is not null)
            {
                try
                {
                    await _activeStrategy.DisconnectAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Active VPN shutdown cleanup failed: {ex.Message}");
                }
            }

            await DisconnectStaleTunnelsAsync(cancellationToken);

            var forceResponse = await _vpnServiceClient.SendAsync(new VpnServiceRequest
            {
                Command = VpnCommandType.ForceDisconnectAll
            }, cancellationToken);

            if (!forceResponse.Success || forceResponse.TunnelActive)
            {
                throw new InvalidOperationException(BuildTeardownFailureMessage(
                    forceResponse,
                    "VPN service could not verify tunnel teardown."));
            }

            var statusResponse = await _vpnServiceClient.SendAsync(new VpnServiceRequest
            {
                Command = VpnCommandType.GetTunnelStatus
            }, cancellationToken);

            if (!statusResponse.Success || statusResponse.TunnelActive)
            {
                throw new InvalidOperationException(BuildTeardownFailureMessage(
                    statusResponse,
                    "VPN tunnel is still active after disconnect."));
            }

            // Wipe credentials only after service-side teardown is confirmed.
            if (_activeCertificateName is not null)
            {
                _configStorage.DeleteConfig(_activeCertificateName);
                _activeCertificateName = null;
            }

            _activeStrategy = null;
            _activeConfigPath = null;
            _activePassphrase = null;
            _activeServerIp = null;
            VpnIpAddress = null;
            CurrentStats = null;
            _currentServer = null;
            SetStatus(ConnectionStatus.Disconnected);
        }
        catch (Exception ex)
        {
            var message = $"VPN disconnect could not be verified: {ex.Message}";
            _logger.LogError(message, ex);
            SetError(message);
            throw new InvalidOperationException(message, ex);
        }
        finally
        {
            _intentionalDisconnect = false;
        }
    }

    private async Task DisconnectStaleTunnelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _openVpnStrategy.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Best-effort OpenVPN shutdown cleanup failed: {ex.Message}");
        }

        try
        {
            await _ikev2Strategy.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Best-effort IKEv2 shutdown cleanup failed: {ex.Message}");
        }
    }

    private static string BuildTeardownFailureMessage(VpnServiceResponse response, string fallback)
    {
        var detail = string.Join(Environment.NewLine,
            new[] { response.ErrorMessage, response.TunnelStatus, response.Output }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(detail) ? fallback : detail;
    }

    public void Dispose()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _statsTimer.Stop();
        _statsTimer.Dispose();
        _openVpnStrategy.ConnectionStateChanged -= OnOpenVpnStateChanged;
        _openVpnStrategy.Dispose();

        // Best-effort cleanup of stored configs
        if (_activeCertificateName is not null)
            _configStorage.DeleteConfig(_activeCertificateName);
    }

    private IVpnTunnelStrategy SelectStrategy(VpnProtocol protocol) => protocol switch
    {
        VpnProtocol.OpenVPN => _openVpnStrategy,
        VpnProtocol.IKEv2 => _ikev2Strategy,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private long _lastBytesIn;
    private long _lastBytesOut;
    private DateTime _lastStatsTime;

    private void OnStatsTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        // Check if the tunnel strategy reports disconnection
        if (_activeStrategy is not null && !_activeStrategy.IsConnected)
        {
            // For OpenVPN with auto-reconnect, trigger reconnect loop instead of immediate disconnect
            if (_activeProtocol == VpnProtocol.OpenVPN && _openVpnSettings.AutoReconnect && _activeConfigPath is not null)
            {
                _ = TryReconnectWithBackoffAsync();
            }
            else
            {
                _ = DisconnectAsync();
            }
            return;
        }

        var duration = DateTime.UtcNow - _connectedSince;
        var now = DateTime.UtcNow;
        var timeDiff = (now - _lastStatsTime).TotalSeconds;
        if (timeDiff <= 0) timeDiff = 1;

        long currentBytesIn = _activeStrategy?.BytesIn ?? 0;
        long currentBytesOut = _activeStrategy?.BytesOut ?? 0;

        // Calculate speeds in Mbps
        double downloadSpeedMbps = ((currentBytesIn - _lastBytesIn) * 8.0) / (1024 * 1024 * timeDiff);
        double uploadSpeedMbps = ((currentBytesOut - _lastBytesOut) * 8.0) / (1024 * 1024 * timeDiff);

        // Prevent negative speeds if counters reset
        if (downloadSpeedMbps < 0) downloadSpeedMbps = 0;
        if (uploadSpeedMbps < 0) uploadSpeedMbps = 0;

        _lastBytesIn = currentBytesIn;
        _lastBytesOut = currentBytesOut;
        _lastStatsTime = now;

        double sessionDataMb = (currentBytesIn + currentBytesOut) / (1024.0 * 1024.0);
        double sessionDownloadMb = currentBytesIn / (1024.0 * 1024.0);
        double sessionUploadMb = currentBytesOut / (1024.0 * 1024.0);

        var stats = new ConnectionStats(
            DownloadSpeedMbps: downloadSpeedMbps,
            UploadSpeedMbps: uploadSpeedMbps,
            SessionDataMb: sessionDataMb,
            Duration: duration,
            SessionDownloadMb: sessionDownloadMb,
            SessionUploadMb: sessionUploadMb);

        CurrentStats = stats;

        // Ensure VPN IP is always populated (e.g. after reconnect).
        if (VpnIpAddress is null)
            VpnIpAddress = _activeServerIp;

        StatsUpdated?.Invoke(this, stats);
    }

    /// <summary>
    /// Handles OpenVPN management interface state changes and maps to ConnectionStatus.
    /// Triggers reconnect loop for unexpected disconnections when auto-reconnect is enabled.
    /// </summary>
    private void OnOpenVpnStateChanged(object? sender, OpenVpnConnectionState state)
    {
        switch (state)
        {
            case OpenVpnConnectionState.Connected:
                _connectedSince = DateTime.UtcNow;
                _lastBytesIn = 0;
                _lastBytesOut = 0;
                _lastStatsTime = DateTime.UtcNow;
                VpnIpAddress = _activeServerIp;
                SetStatus(ConnectionStatus.Connected);
                _statsTimer.Start();
                break;

            case OpenVpnConnectionState.Reconnecting:
                _statsTimer.Stop();
                SetStatus(ConnectionStatus.Reconnecting);
                break;

            case OpenVpnConnectionState.AuthFailed:
                SetError("VPN authentication failed. Check your credentials or request a new configuration.");
                break;

            case OpenVpnConnectionState.TapMissing:
                SetError("TAP network adapter not found. Install the OpenVPN TAP driver.");
                break;

            case OpenVpnConnectionState.CertificateError:
                SetError("VPN certificate is invalid or expired. Fetch a new configuration.");
                break;

            case OpenVpnConnectionState.Error:
                SetError("OpenVPN encountered a fatal error. Check the connection logs.");
                break;

            case OpenVpnConnectionState.Disconnected when Status == ConnectionStatus.Connected:
                // Unexpected disconnect - try to reconnect if enabled.
                // Skip if the disconnect was intentional (user or explicit DisconnectAsync call).
                if (_intentionalDisconnect)
                    break;
                if (_openVpnSettings.AutoReconnect && _activeConfigPath is not null)
                    _ = TryReconnectWithBackoffAsync();
                else
                    _ = DisconnectAsync();
                break;
        }
    }

    /// <summary>
    /// Attempts to reconnect to the VPN using exponential backoff.
    /// Uses the stored config path and passphrase from the last successful connection setup.
    /// </summary>
    private async Task TryReconnectWithBackoffAsync()
    {
        if (_activeConfigPath is null || _activeServerIp is null)
        {
            await DisconnectAsync();
            return;
        }

        // Avoid multiple concurrent reconnect loops
        if (_reconnectCts is not null)
            return;

        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;
        var backoff = _openVpnSettings.ReconnectBackoffSeconds;
        var maxAttempts = _openVpnSettings.MaxReconnectAttempts;

        SetStatus(ConnectionStatus.Reconnecting);
        _logger.LogWarning("VPN connection lost. Starting reconnect loop.");

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                break;

            var delaySec = attempt < backoff.Length ? backoff[attempt] : backoff[^1];
            _logger.LogInformation($"Reconnect attempt {attempt + 1}/{maxAttempts} in {delaySec}s");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);

                await _openVpnStrategy.ConnectAsync(_activeConfigPath, _activePassphrase, _activeServerIp, ct);

                // If we reach here, reconnect succeeded
                _connectedSince = DateTime.UtcNow;
                VpnIpAddress = _activeServerIp;
                SetStatus(ConnectionStatus.Connected);
                _statsTimer.Start();
                _logger.LogInformation("VPN reconnected successfully.");
                return;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Reconnect attempt {attempt + 1} failed: {ex.Message}");
            }
        }

        // All attempts exhausted or cancelled
        _logger.LogError("VPN reconnection failed after all attempts.");
        await CleanupAsync(CancellationToken.None);
        SetError($"VPN reconnection failed after {maxAttempts} attempts. Please reconnect manually.");
    }

    private void SetError(string message)
    {
        LastErrorMessage = message;
        SetStatus(ConnectionStatus.Error);
        ErrorOccurred?.Invoke(this, message);
    }

    internal static string ClassifyConnectionError(Exception ex)
    {
        var message = ex.Message;

        if (ex is VpnConfigRequestException configError)
            return ClassifyVpnConfigRequestError(configError);

        if (ex is OpenVpnSetupException)
            return message;

        if (ex is FileNotFoundException)
            return "OpenVPN is not installed. LibreGuard can install or repair OpenVPN from the bundled setup package.";

        if (message.Contains("TAP", StringComparison.OrdinalIgnoreCase))
            return "TAP network adapter not found. Install the OpenVPN TAP driver.";

        if (message.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
            return "VPN authentication failed. Check your credentials or request a new configuration.";

        if (message.Contains("certificate", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("VERIFY ERROR", StringComparison.OrdinalIgnoreCase)))
            return "VPN certificate is invalid or expired. Fetch a new configuration.";

        if (message.Contains("data limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("monthly limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return message;

        if (message.Contains("Windows did not expose", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("DNS configuration", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("usable LibreGuard VPN interface", StringComparison.OrdinalIgnoreCase))
            return message;

        if (message.Contains("subscription", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.Ordinal))
            return "OpenVPN requires a Pro subscription. Upgrade your plan to use this protocol.";

        if (message.Contains("401", StringComparison.Ordinal) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return "Session expired. Please log in again.";

        if (message.Contains("404", StringComparison.Ordinal))
            return "VPN configuration not available for this server.";

        if (message.Contains("429", StringComparison.Ordinal))
            return "VPN configuration retrieval is busy. Please retry shortly.";

        if (message.Contains("409", StringComparison.Ordinal))
            return "VPN configuration is not ready for this device. Please sign in again or request a new configuration.";

        return $"Connection failed: {message}";
    }

    private static string ClassifyVpnConfigRequestError(VpnConfigRequestException ex)
    {
        var errorCode = ex.ErrorCode ?? string.Empty;

        if (string.Equals(errorCode, "DEVICE_KEY_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "This device still needs VPN key registration. Please sign out, sign in again, and retry.";

        if (string.Equals(errorCode, "PASSPHRASE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
            return "This IKEv2 certificate cannot be used because its passphrase is unavailable. Request a new certificate or contact support.";

        if (string.Equals(errorCode, "VPN_CONFIG_BUSY", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "CERTIFICATE_REQUEST_BUSY", StringComparison.OrdinalIgnoreCase) ||
            ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return ex.RetryAfter is { TotalSeconds: > 0 }
                ? $"VPN configuration retrieval is busy. Please retry in {Math.Ceiling(ex.RetryAfter.Value.TotalSeconds):F0} seconds."
                : "VPN configuration retrieval is busy. Please retry shortly.";
        }

        if (string.Equals(errorCode, "DEVICE_NOT_REGISTERED", StringComparison.OrdinalIgnoreCase) ||
            ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return "Session expired or this device is not registered. Please sign in again.";

        if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            if (ex.BackendMessage?.Contains("Pro subscription", StringComparison.OrdinalIgnoreCase) == true ||
                ex.BackendMessage?.Contains("requires a Pro", StringComparison.OrdinalIgnoreCase) == true)
                return "This server or protocol requires a Pro subscription.";

            return ex.BackendMessage ?? "Access to this VPN configuration was denied.";
        }

        if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            return ex.BackendMessage ?? "VPN configuration not available for this server.";

        if (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            return ex.BackendMessage ?? "VPN configuration is not ready for this device. Please retry shortly.";

        return ex.BackendMessage ?? $"VPN configuration request failed with status {(int)ex.StatusCode}.";
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        if (_activeStrategy is not null)
        {
            try { await _activeStrategy.DisconnectAsync(ct); } catch { /* best-effort */ }
            _activeStrategy = null;
        }

        if (_activeCertificateName is not null)
        {
            _configStorage.DeleteConfig(_activeCertificateName);
            _activeCertificateName = null;
        }

        _activeConfigPath = null;
        _activePassphrase = null;
        _activeServerIp = null;
        VpnIpAddress = null;
        CurrentStats = null;
    }
}
