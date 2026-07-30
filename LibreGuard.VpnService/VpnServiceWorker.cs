using System.IO.Pipes;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreGuard.VpnService.Contracts;

namespace LibreGuard.VpnService;

/// <summary>
/// Background service that listens on a named pipe for VPN commands from the WPF app.
/// Runs as LocalSystem — all operations execute with full privileges (no UAC).
/// </summary>
internal sealed class VpnServiceWorker : BackgroundService
{
    private const string PipeName = "LibreGuardVpnService";
    private const string VpnConnectionName = "LibreGuard VPN";
    private static readonly TimeSpan OwnerWatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServiceStopTeardownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TeardownVerificationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TeardownVerificationInterval = TimeSpan.FromMilliseconds(250);
    private const int TeardownAttempts = 2;
    private readonly VpnCommandHandler _handler;
    private readonly OpenVpnProcessManager _openVpn;
    private readonly ILogger<VpnServiceWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SemaphoreSlim _teardownLock = new(1, 1);
    private readonly object _ownerLock = new();
    private int? _ownerProcessId;
    private DateTime? _ownerProcessStartTimeUtc;
    private volatile bool _shutdownTeardownCompleted;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public VpnServiceWorker(
        VpnCommandHandler handler,
        OpenVpnProcessManager openVpn,
        ILogger<VpnServiceWorker> logger,
        IHostApplicationLifetime lifetime)
    {
        _handler = handler;
        _openVpn = openVpn;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LibreGuard VPN Service started, listening on pipe: {PipeName}",
            PipeName);

        await RecoverOrphanedTunnelAsync(stoppingToken);

        var watchdogTask = RunOwnerWatchdogAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Create a new pipe server for each connection (one client at a time)
            var pipeSecurity = CreatePipeSecurity();
            await using var pipeServer = NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 65536,
                outBufferSize: 65536,
                pipeSecurity);

            try
            {
                await pipeServer.WaitForConnectionAsync(stoppingToken);
                _logger.LogInformation("Client connected to pipe");

                await HandleClientAsync(pipeServer, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling pipe client");
            }
        }

        await watchdogTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_shutdownTeardownCompleted)
        {
            using var teardownCts = new CancellationTokenSource(ServiceStopTeardownTimeout);
            try
            {
                await ForceDisconnectAllAsync(teardownCts.Token, "service stopping");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VPN teardown failed while LibreGuard VPN Service was stopping");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        VpnServiceResponse response;

        try
        {
            // Read length-prefixed message
            var lengthBuf = new byte[4];
            var bytesRead = await ReadExactAsync(pipe, lengthBuf, ct);
            if (bytesRead < 4)
            {
                _logger.LogWarning("Client disconnected before sending request length (read {BytesRead} of 4)", bytesRead);
                return; // client hung up — nothing to respond to
            }

            var messageLength = BitConverter.ToInt32(lengthBuf, 0);
            if (messageLength is <= 0 or > 1_048_576) // 1 MB max
            {
                _logger.LogWarning("Invalid message length: {Length}", messageLength);
                response = new VpnServiceResponse { Success = false, ErrorMessage = $"Invalid message length: {messageLength}" };
                await WriteResponseAsync(pipe, response, ct);
                return;
            }

            var messageBuf = new byte[messageLength];
            bytesRead = await ReadExactAsync(pipe, messageBuf, ct);
            if (bytesRead < messageLength)
            {
                _logger.LogWarning("Truncated request: expected {Expected}, got {Actual}", messageLength, bytesRead);
                response = new VpnServiceResponse { Success = false, ErrorMessage = "Truncated request" };
                await WriteResponseAsync(pipe, response, ct);
                return;
            }

            var request = JsonSerializer.Deserialize<VpnServiceRequest>(messageBuf, JsonOptions);
            if (request is null)
            {
                _logger.LogWarning("Failed to deserialize request");
                response = new VpnServiceResponse { Success = false, ErrorMessage = "Failed to deserialize request" };
                await WriteResponseAsync(pipe, response, ct);
                return;
            }

            _logger.LogInformation("Received command: {Command}", request.Command);
            response = await ProcessCommandAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pipe message");
            response = new VpnServiceResponse { Success = false, ErrorMessage = ex.Message };
        }

        try
        {
            await WriteResponseAsync(pipe, response, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write response to pipe");
        }
    }

    private static async Task WriteResponseAsync(NamedPipeServerStream pipe, VpnServiceResponse response, CancellationToken ct)
    {
        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var responseLengthBytes = BitConverter.GetBytes(responseBytes.Length);
        await pipe.WriteAsync(responseLengthBytes, ct);
        await pipe.WriteAsync(responseBytes, ct);
        await pipe.FlushAsync(ct);
        pipe.WaitForPipeDrain();
    }

    private async Task<VpnServiceResponse> ProcessCommandAsync(VpnServiceRequest request, CancellationToken ct)
    {
        try
        {
            switch (request.Command)
            {
                case VpnCommandType.Ping:
                    return new VpnServiceResponse { Success = true, Output = "pong" };

                case VpnCommandType.ImportCertificates:
                {
                    if (string.IsNullOrEmpty(request.PfxBase64))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "PfxBase64 is required" };

                    var pfxBytes = Convert.FromBase64String(request.PfxBase64);
                    var (clientThumb, caThumb) = _handler.ImportCertificates(pfxBytes, request.PfxPassword);

                    if (clientThumb is null)
                        return new VpnServiceResponse { Success = false, ErrorMessage = "No client certificate with private key found in PFX" };

                    return new VpnServiceResponse
                    {
                        Success = true,
                        ClientThumbprint = clientThumb,
                        CaThumbprint = caThumb
                    };
                }

                case VpnCommandType.ImportTrustedRootCertificate:
                {
                    if (string.IsNullOrEmpty(request.TrustedRootCertificateBase64))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "TrustedRootCertificateBase64 is required" };

                    var certBytes = Convert.FromBase64String(request.TrustedRootCertificateBase64);
                    var trustedRootThumb = _handler.ImportTrustedRootCertificate(certBytes);

                    return new VpnServiceResponse
                    {
                        Success = true,
                        TrustedRootThumbprint = trustedRootThumb
                    };
                }

                case VpnCommandType.CreateConnection:
                {
                    if (string.IsNullOrEmpty(request.ConnectionName) || string.IsNullOrEmpty(request.ServerAddress))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "ConnectionName and ServerAddress are required" };

                    var (exitCode, output, error) = await _handler.CreateConnectionAsync(
                        request.ConnectionName, request.ServerAddress, ct);

                    return new VpnServiceResponse
                    {
                        Success = exitCode == 0,
                        ExitCode = exitCode,
                        Output = !string.IsNullOrWhiteSpace(error) ? error : output,
                        ErrorMessage = exitCode != 0 ? $"Failed to create VPN connection (exit {exitCode})" : null
                    };
                }

                case VpnCommandType.SetIpsecPolicy:
                {
                    if (string.IsNullOrEmpty(request.ConnectionName))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "ConnectionName is required" };

                    var (exitCode, output, error) = await _handler.SetIpsecPolicyAsync(request.ConnectionName, ct);

                    return new VpnServiceResponse
                    {
                        Success = exitCode == 0,
                        ExitCode = exitCode,
                        Output = !string.IsNullOrWhiteSpace(error) ? error : output,
                        ErrorMessage = exitCode != 0 ? $"Failed to set IPsec policy (exit {exitCode})" : null
                    };
                }

                case VpnCommandType.SetDnsServers:
                {
                    if (string.IsNullOrEmpty(request.ConnectionName))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "ConnectionName is required" };

                    if (request.DnsServers is not { Length: > 0 })
                        return new VpnServiceResponse { Success = false, ErrorMessage = "DnsServers is required" };

                    var (exitCode, output, error) = await _handler.SetDnsServersAsync(
                        request.ConnectionName, request.DnsServers, request.VpnInterfaceIndex, ct);

                    return new VpnServiceResponse
                    {
                        Success = exitCode == 0,
                        ExitCode = exitCode,
                        Output = !string.IsNullOrWhiteSpace(error) ? error : output,
                        ErrorMessage = exitCode != 0
                            ? (!string.IsNullOrWhiteSpace(error)
                                ? error.Trim()
                                : !string.IsNullOrWhiteSpace(output)
                                    ? output.Trim()
                                    : $"Failed to set DNS servers (exit {exitCode})")
                            : null
                    };
                }

                case VpnCommandType.Dial:
                {
                    if (string.IsNullOrEmpty(request.ConnectionName))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "ConnectionName is required" };

                    var (exitCode, output, error) = await _handler.DialAsync(request.ConnectionName, ct);
                    if (exitCode == 0)
                        RegisterTunnelOwner(request);

                    return new VpnServiceResponse
                    {
                        Success = exitCode == 0,
                        ExitCode = exitCode,
                        Output = $"{output} {error}".Trim(),
                        ErrorMessage = exitCode != 0 ? $"rasdial failed (exit {exitCode})" : null
                    };
                }

                case VpnCommandType.Disconnect:
                {
                    if (string.IsNullOrEmpty(request.ConnectionName))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "ConnectionName is required" };

                    var (ikev2Active, exitCode, output) = await _handler.DisconnectAndVerifyAsync(request.ConnectionName, ct);
                    var status = await GetTunnelStatusResponseAsync(ct);
                    if (!status.TunnelActive)
                        await ClearVerifiedTunnelStateAsync(ct);

                    return new VpnServiceResponse
                    {
                        Success = !ikev2Active,
                        ExitCode = exitCode,
                        Output = output,
                        TunnelActive = status.TunnelActive,
                        OpenVpnActive = status.OpenVpnActive,
                        IkeV2Active = ikev2Active,
                        TunnelStatus = status.TunnelStatus,
                        ErrorMessage = ikev2Active
                            ? "IKEv2 tunnel is still active after disconnect."
                            : null
                    };
                }

                case VpnCommandType.RemoveConnection:
                {
                    if (string.IsNullOrEmpty(request.ConnectionName))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "ConnectionName is required" };

                    var (exitCode, output, error) = await _handler.RemoveConnectionAsync(request.ConnectionName, ct);

                    return new VpnServiceResponse
                    {
                        Success = true, // remove is best-effort
                        ExitCode = exitCode,
                        Output = !string.IsNullOrWhiteSpace(error) ? error : output
                    };
                }

                case VpnCommandType.CleanupCertificates:
                {
                    _handler.CleanupCertificates(request.ClientThumbprint, request.CaThumbprint);
                    return new VpnServiceResponse { Success = true };
                }

                case VpnCommandType.StartOpenVpn:
                {
                    if (string.IsNullOrWhiteSpace(request.OpenVpnConfigContent))
                        return new VpnServiceResponse { Success = false, ErrorMessage = "OpenVpnConfigContent is required" };

                    await _openVpn.StartAsync(request.OpenVpnConfigContent, request.OpenVpnPassphrase, ct);
                    RegisterTunnelOwner(request);

                    return new VpnServiceResponse
                    {
                        Success = true,
                        OpenVpnState = _openVpn.State.ToString(),
                        VpnLocalIp = _openVpn.LocalIp,
                        OpenVpnActive = _openVpn.IsActive,
                        TunnelActive = _openVpn.IsActive
                    };
                }

                case VpnCommandType.StopOpenVpn:
                {
                    await _openVpn.StopAsync(ct);
                    await ClearOwnerIfNoTunnelAsync(ct);
                    return new VpnServiceResponse
                    {
                        Success = true,
                        OpenVpnState = _openVpn.State.ToString(),
                        OpenVpnActive = _openVpn.IsActive
                    };
                }

                case VpnCommandType.GetOpenVpnStatus:
                {
                    return new VpnServiceResponse
                    {
                        Success = true,
                        OpenVpnState = _openVpn.State.ToString(),
                        BytesIn = _openVpn.BytesIn,
                        BytesOut = _openVpn.BytesOut,
                        VpnLocalIp = _openVpn.LocalIp,
                        OpenVpnActive = _openVpn.IsActive,
                        TunnelActive = _openVpn.IsActive
                    };
                }

                case VpnCommandType.GetOpenVpnHealth:
                {
                    var health = _openVpn.GetHealth();
                    return new VpnServiceResponse
                    {
                        Success = true,
                        OpenVpnInstalled = health.OpenVpnInstalled,
                        OpenVpnExePath = health.OpenVpnExePath,
                        OpenVpnDriverInstalled = health.OpenVpnDriverInstalled,
                        SetupRequiredReason = health.SetupRequiredReason
                    };
                }

                case VpnCommandType.GetTunnelStatus:
                {
                    return await GetTunnelStatusResponseAsync(ct);
                }

                case VpnCommandType.ForceDisconnectAll:
                {
                    return await ForceDisconnectAllAsync(ct, "client request");
                }

                case VpnCommandType.ShutdownService:
                {
                    return await ShutdownServiceAsync(ct);
                }

                case VpnCommandType.EnableKillSwitch:
                {
                    await _handler.EnableKillSwitchAsync(request.VpnServerIp, request.VpnLocalIp, ct);
                    return new VpnServiceResponse { Success = true };
                }

                case VpnCommandType.DisableKillSwitch:
                {
                    await _handler.DisableKillSwitchAsync(ct);
                    return new VpnServiceResponse { Success = true };
                }

                default:
                    return new VpnServiceResponse { Success = false, ErrorMessage = $"Unknown command: {request.Command}" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {Command} failed", request.Command);

            // For OpenVPN commands, include recent process output for debugging
            var output = request.Command is VpnCommandType.StartOpenVpn or VpnCommandType.StopOpenVpn
                ? _openVpn.GetRecentOutput()
                : null;

            return new VpnServiceResponse
            {
                Success = false,
                ErrorMessage = ex.Message,
                Output = output,
                OpenVpnState = request.Command is VpnCommandType.StartOpenVpn or VpnCommandType.StopOpenVpn or VpnCommandType.GetOpenVpnStatus
                    ? _openVpn.State.ToString()
                    : null
            };
        }
    }

    private void RegisterTunnelOwner(VpnServiceRequest request)
    {
        if (request.ClientProcessId is null || request.ClientProcessStartTimeUtc is null)
            return;

        lock (_ownerLock)
        {
            _ownerProcessId = request.ClientProcessId;
            _ownerProcessStartTimeUtc = request.ClientProcessStartTimeUtc.Value;
        }

        _logger.LogInformation(
            "Registered tunnel owner PID={ProcessId}, Start={StartTimeUtc:O}",
            request.ClientProcessId,
            request.ClientProcessStartTimeUtc.Value);
    }

    private async Task RunOwnerWatchdogAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(OwnerWatchdogInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!TryGetTunnelOwner(out var processId, out var startTimeUtc))
                    continue;

                if (IsOwnerProcessAlive(processId, startTimeUtc))
                    continue;

                _logger.LogWarning(
                    "Tunnel owner PID={ProcessId} is gone; forcing VPN teardown.",
                    processId);

                await ForceDisconnectAllAsync(CancellationToken.None, "owner process exited");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private bool TryGetTunnelOwner(out int processId, out DateTime startTimeUtc)
    {
        lock (_ownerLock)
        {
            processId = _ownerProcessId ?? 0;
            startTimeUtc = _ownerProcessStartTimeUtc ?? default;
            return _ownerProcessId is not null && _ownerProcessStartTimeUtc is not null;
        }
    }

    private static bool IsOwnerProcessAlive(int processId, DateTime startTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime() == startTimeUtc;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void ClearTunnelOwner()
    {
        lock (_ownerLock)
        {
            _ownerProcessId = null;
            _ownerProcessStartTimeUtc = null;
        }
    }

    private async Task ClearOwnerIfNoTunnelAsync(CancellationToken ct)
    {
        var ikev2Active = await _handler.IsConnectionActiveAsync(VpnConnectionName, ct);
        if (!_openVpn.IsActive && !ikev2Active)
            await ClearVerifiedTunnelStateAsync(ct);
    }

    private async Task ClearVerifiedTunnelStateAsync(CancellationToken ct)
    {
        ClearTunnelOwner();
        try
        {
            await _handler.DisableKillSwitchAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove Kill Switch rules after verified VPN teardown");
        }
    }

    private async Task<VpnServiceResponse> ForceDisconnectAllAsync(CancellationToken ct, string reason)
    {
        await _teardownLock.WaitAsync(ct);
        try
        {
            _logger.LogWarning("ForceDisconnectAll started: {Reason}", reason);

            string? openVpnError = null;
            string? ikev2Output = null;

            for (var attempt = 1; attempt <= TeardownAttempts; attempt++)
            {
                try
                {
                    await _openVpn.StopAsync(CancellationToken.None);
                    openVpnError = null;
                    break;
                }
                catch (Exception ex)
                {
                    openVpnError = ex.Message;
                    _logger.LogError(
                        ex,
                        "ForceDisconnectAll OpenVPN stop attempt {Attempt}/{Attempts} failed",
                        attempt,
                        TeardownAttempts);

                    if (attempt < TeardownAttempts && _openVpn.IsActive)
                        await Task.Delay(TeardownVerificationInterval, ct);
                }
            }

            for (var attempt = 1; attempt <= TeardownAttempts; attempt++)
            {
                try
                {
                    var (active, _, output) = await _handler.DisconnectAndVerifyAsync(VpnConnectionName, ct);
                    ikev2Output = output;
                    if (!active)
                        break;

                    _logger.LogWarning(
                        "ForceDisconnectAll IKEv2 disconnect attempt {Attempt}/{Attempts} still reports an active tunnel",
                        attempt,
                        TeardownAttempts);
                }
                catch (Exception ex)
                {
                    ikev2Output = ex.Message;
                    _logger.LogError(
                        ex,
                        "ForceDisconnectAll IKEv2 disconnect attempt {Attempt}/{Attempts} failed",
                        attempt,
                        TeardownAttempts);
                }

                if (attempt < TeardownAttempts)
                    await Task.Delay(TeardownVerificationInterval, ct);
            }

            var response = await WaitForTunnelInactiveAsync(ct);
            if (!response.TunnelActive)
                await ClearVerifiedTunnelStateAsync(ct);

            return response with
            {
                Success = !response.TunnelActive,
                ErrorMessage = response.TunnelActive
                    ? "One or more VPN tunnels are still active after forced disconnect."
                    : null,
                Output = string.Join(Environment.NewLine, new[] { openVpnError, ikev2Output }.Where(s => !string.IsNullOrWhiteSpace(s)))
            };
        }
        finally
        {
            _teardownLock.Release();
        }
    }

    private async Task<VpnServiceResponse> WaitForTunnelInactiveAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            var status = await GetTunnelStatusResponseAsync(ct);
            if (!status.TunnelActive ||
                Stopwatch.GetElapsedTime(started) >= TeardownVerificationTimeout)
            {
                return status;
            }

            await Task.Delay(TeardownVerificationInterval, ct);
        }
    }

    private async Task RecoverOrphanedTunnelAsync(CancellationToken ct)
    {
        try
        {
            var status = await GetTunnelStatusResponseAsync(ct);
            if (!status.TunnelActive)
            {
                await ClearVerifiedTunnelStateAsync(ct);
                return;
            }

            _logger.LogWarning(
                "Found an orphaned LibreGuard VPN tunnel during service startup: {Status}. Starting verified teardown.",
                status.TunnelStatus);
            await ForceDisconnectAllAsync(ct, "service startup recovery");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover an orphaned LibreGuard VPN tunnel during service startup");
        }
    }

    private async Task<VpnServiceResponse> GetTunnelStatusResponseAsync(CancellationToken ct)
    {
        var openVpnActive = _openVpn.IsActive;
        var ikev2Active = await _handler.IsConnectionActiveAsync(VpnConnectionName, ct);
        var tunnelActive = openVpnActive || ikev2Active;
        var status = $"OpenVPN={_openVpn.State}; IKEv2={(ikev2Active ? "Connected" : "Disconnected")}";

        return new VpnServiceResponse
        {
            Success = true,
            TunnelActive = tunnelActive,
            OpenVpnActive = openVpnActive,
            IkeV2Active = ikev2Active,
            TunnelStatus = status,
            OpenVpnState = _openVpn.State.ToString(),
            BytesIn = _openVpn.BytesIn,
            BytesOut = _openVpn.BytesOut,
            VpnLocalIp = _openVpn.LocalIp
        };
    }

    private async Task<VpnServiceResponse> ShutdownServiceAsync(CancellationToken ct)
    {
        var status = await GetTunnelStatusResponseAsync(ct);
        if (status.TunnelActive)
        {
            _logger.LogWarning(
                "ShutdownService received with an active tunnel: {Status}. Starting verified teardown.",
                status.TunnelStatus);
            status = await ForceDisconnectAllAsync(ct, "service shutdown request");

            if (!status.Success || status.TunnelActive)
            {
                _logger.LogError(
                    "ShutdownService could not verify tunnel teardown: {Status}",
                    status.TunnelStatus);
                return status with
                {
                    Success = false,
                    ErrorMessage = status.ErrorMessage ??
                        "VPN service could not verify tunnel teardown during shutdown."
                };
            }

            // ForceDisconnectAll already completed the final tunnel and Kill Switch cleanup.
            // StopAsync must not launch the same teardown transaction a second time.
            _shutdownTeardownCompleted = true;
        }

        ClearTunnelOwner();
        _logger.LogInformation("ShutdownService accepted; stopping LibreGuard VPN Service host.");

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            _lifetime.StopApplication();
        }, CancellationToken.None);

        return status with
        {
            Success = true,
            Output = "LibreGuard VPN Service shutdown requested."
        };
    }

    /// <summary>
    /// Creates pipe security that allows the current interactive user to connect.
    /// The service runs as SYSTEM; the WPF app runs as the logged-in user.
    /// </summary>
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();

        // Allow authenticated users to read/write (connect and send/receive messages)
        security.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            System.Security.AccessControl.AccessControlType.Allow));

        // Allow SYSTEM full control (the service account)
        security.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));

        return security;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (bytesRead == 0) break;
            totalRead += bytesRead;
        }

        return totalRead;
    }
}

