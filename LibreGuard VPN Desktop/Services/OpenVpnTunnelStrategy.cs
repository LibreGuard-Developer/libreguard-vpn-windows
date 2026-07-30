using System.Diagnostics;
using System.IO;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Establishes an OpenVPN tunnel by delegating to the LibreGuard VPN Service over a named pipe.
/// The service (LocalSystem) spawns openvpn.exe, manages the management interface, and handles
/// TAP/Wintun driver operations - no UAC prompts or bundled binaries needed in the desktop app.
/// </summary>
internal sealed class OpenVpnTunnelStrategy : IVpnTunnelStrategy, IDisposable
{
    private readonly IVpnServiceClient _serviceClient;
    private CancellationTokenSource? _pollCts;
    private volatile bool _isConnected;
    private volatile OpenVpnConnectionState _connectionState = OpenVpnConnectionState.Disconnected;
    private long _bytesIn;
    private long _bytesOut;
    private volatile string? _localIp;

    public VpnProtocol Protocol => VpnProtocol.OpenVPN;

    public bool IsConnected => _isConnected;

    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public string? LocalIp => _localIp;

    /// <summary>
    /// Current detailed connection state received from the service.
    /// </summary>
    internal OpenVpnConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// Raised when the OpenVPN connection state changes (polled from the service).
    /// </summary>
    internal event EventHandler<OpenVpnConnectionState>? ConnectionStateChanged;

    public OpenVpnTunnelStrategy(IVpnServiceClient serviceClient)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        _serviceClient = serviceClient;
    }

    /// <summary>
    /// Sends the .ovpn config content to the VPN service which spawns openvpn.exe as LocalSystem.
    /// Waits for the service to report CONNECTED state before returning.
    /// </summary>
    public async Task ConnectAsync(string configPath, string? passphrase, string serverIp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configPath);

        await DisconnectAsync(ct);

        // Read the .ovpn config content - the service needs the content, not a path
        // (the service runs in a different process/session and can't access user temp files)
        var configContent = OpenVpnConfigParser.NormalizeForLaunch(await File.ReadAllTextAsync(configPath, ct));
        if (!OpenVpnConfigParser.ValidateMinimalStructure(configContent))
            throw new InvalidOperationException("OpenVPN configuration is invalid or incomplete. Request a new configuration and try again.");

        Debug.WriteLine($"[OpenVPN] Sending StartOpenVpn to service (config={configContent.Length} chars, server={serverIp})");

        var response = await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.StartOpenVpn,
            OpenVpnConfigContent = configContent,
            OpenVpnPassphrase = passphrase,
            ServerAddress = serverIp
        }, ct);

        Debug.WriteLine($"[OpenVPN] Service response: Success={response.Success}, State={response.OpenVpnState}");

        if (!response.Success)
        {
            Debug.WriteLine($"[OpenVPN] Error: {response.ErrorMessage}");
            if (!string.IsNullOrWhiteSpace(response.Output))
                Debug.WriteLine($"[OpenVPN] Service output:\n{response.Output}");

            var errorDetail = response.ErrorMessage ?? "Failed to start OpenVPN tunnel.";
            if (!string.IsNullOrWhiteSpace(response.Output))
                errorDetail += $"\n--- Service output ---\n{response.Output}";

            throw new InvalidOperationException(errorDetail);
        }

        _isConnected = true;
        _localIp = response.VpnLocalIp;
        SetConnectionState(ParseState(response.OpenVpnState));

        // Start background polling for state changes (detect unexpected disconnects)
        StartStatusPolling();
    }

    /// <summary>
    /// Tells the VPN service to gracefully stop the OpenVPN process.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        StopStatusPolling();

        var response = await _serviceClient.SendAsync(new VpnServiceRequest
        {
            Command = VpnCommandType.StopOpenVpn
        }, ct);

        if (!response.Success)
            throw new InvalidOperationException(response.ErrorMessage ?? "Failed to stop OpenVPN tunnel.");

        _isConnected = false;
        SetConnectionState(OpenVpnConnectionState.Disconnected);
    }

    public void Dispose()
    {
        StopStatusPolling();
    }

    #region Status Polling

    /// <summary>
    /// Periodically polls the service for the current OpenVPN state.
    /// Detects unexpected disconnects and fires <see cref="ConnectionStateChanged"/>.
    /// </summary>
    private void StartStatusPolling()
    {
        StopStatusPolling();
        _pollCts = new CancellationTokenSource();
        _ = PollStatusLoopAsync(_pollCts.Token);
    }

    private void StopStatusPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private async Task PollStatusLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);

                var response = await _serviceClient.SendAsync(new VpnServiceRequest
                {
                    Command = VpnCommandType.GetOpenVpnStatus
                }, ct);

                if (response.Success)
                {
                    var state = ParseState(response.OpenVpnState);
                    SetConnectionState(state);

                    _isConnected = state == OpenVpnConnectionState.Connected;
                    Interlocked.Exchange(ref _bytesIn, response.BytesIn);
                    Interlocked.Exchange(ref _bytesOut, response.BytesOut);
                    _localIp = response.VpnLocalIp;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during disconnect/dispose
        }
        catch (Exception)
        {
            // Service unavailable - assume disconnected
            _isConnected = false;
            SetConnectionState(OpenVpnConnectionState.Disconnected);
        }
    }

    #endregion

    #region Helpers

    private void SetConnectionState(OpenVpnConnectionState state)
    {
        if (_connectionState == state)
            return;

        _connectionState = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    private static OpenVpnConnectionState ParseState(string? state) => state switch
    {
        "Connected" => OpenVpnConnectionState.Connected,
        "Connecting" => OpenVpnConnectionState.Connecting,
        "Authenticating" => OpenVpnConnectionState.Authenticating,
        "Reconnecting" => OpenVpnConnectionState.Reconnecting,
        "Disconnecting" => OpenVpnConnectionState.Disconnecting,
        "Disconnected" => OpenVpnConnectionState.Disconnected,
        "AuthFailed" => OpenVpnConnectionState.AuthFailed,
        "TapMissing" => OpenVpnConnectionState.TapMissing,
        "CertificateError" => OpenVpnConnectionState.CertificateError,
        "Error" => OpenVpnConnectionState.Error,
        _ => OpenVpnConnectionState.Unknown
    };

    #endregion
}

/// <summary>
/// Detailed OpenVPN connection states received from the VPN service.
/// </summary>
internal enum OpenVpnConnectionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    Reconnecting,
    Disconnecting,
    AuthRequired,
    AuthFailed,
    TapMissing,
    CertificateError,
    Error,
    Unknown
}
