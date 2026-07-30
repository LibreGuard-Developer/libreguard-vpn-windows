using System.IO;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Coordinates verified VPN teardown during application shutdown.
/// </summary>
public sealed class VpnShutdownService
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(45);

    private readonly IVpnConnectionService _vpnConnectionService;
    private readonly IVpnServiceClient _vpnServiceClient;
    private readonly ILoggerService _logger;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _shutdownGate = new(1, 1);
    private bool _shutdownCompleted;

    public VpnShutdownService(
        IVpnConnectionService vpnConnectionService,
        IVpnServiceClient vpnServiceClient,
        ILoggerService logger)
        : this(vpnConnectionService, vpnServiceClient, logger, DefaultShutdownTimeout)
    {
    }

    internal VpnShutdownService(
        IVpnConnectionService vpnConnectionService,
        IVpnServiceClient vpnServiceClient,
        ILoggerService logger,
        TimeSpan shutdownTimeout)
    {
        ArgumentNullException.ThrowIfNull(vpnConnectionService);
        ArgumentNullException.ThrowIfNull(vpnServiceClient);
        ArgumentNullException.ThrowIfNull(logger);

        _vpnConnectionService = vpnConnectionService;
        _vpnServiceClient = vpnServiceClient;
        _logger = logger;
        _shutdownTimeout = shutdownTimeout;
    }

    public async Task<VpnTunnelStatus> GetTunnelStatusAsync(CancellationToken cancellationToken = default)
    {
        var localActive = _vpnConnectionService.Status != ConnectionStatus.Disconnected;

        try
        {
            var response = await _vpnServiceClient.SendAsync(new VpnServiceRequest
            {
                Command = VpnCommandType.GetTunnelStatus
            }, cancellationToken);

            if (!response.Success)
            {
                return VpnTunnelStatus.Unknown(response.ErrorMessage ?? "Could not verify VPN tunnel status.", localActive);
            }

            return new VpnTunnelStatus(
                // The privileged service queries the actual RAS/OpenVPN state. The WPF
                // connection state can lag while teardown notifications are still in flight,
                // so it must not turn a verified disconnected service result into a false
                // "tunnel still active" shutdown failure.
                IsActive: response.TunnelActive,
                IsUnknown: false,
                OpenVpnActive: response.OpenVpnActive,
                IkeV2Active: response.IkeV2Active,
                Detail: response.TunnelStatus);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            _logger.LogWarning($"Could not verify VPN tunnel status: {ex.Message}");
            return VpnTunnelStatus.Unknown(ex.Message, localActive);
        }
    }

    public async Task<VpnShutdownResult> DisconnectOnExitAsync(
        VpnTunnelStatus? initialStatus = null,
        CancellationToken cancellationToken = default)
    {
        await _shutdownGate.WaitAsync(cancellationToken);
        try
        {
            if (_shutdownCompleted)
                return VpnShutdownResult.Success("VPN shutdown disconnect already completed.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_shutdownTimeout);

            try
            {
                var status = initialStatus ?? await GetTunnelStatusAsync(timeoutCts.Token);
                _logger.LogInformation(status.ShouldWarnOnExit
                    ? "Requesting verified VPN teardown and service shutdown."
                    : "VPN tunnel is already verified disconnected; requesting service shutdown.");

                // ShutdownService owns the entire privileged close transaction. When a tunnel
                // is active, the service tears down both OpenVPN and IKEv2, waits for Windows
                // to report them inactive, and only then accepts its own shutdown. Keeping this
                // in one pipe request avoids canceling a client request while teardown continues
                // inside the service and then queueing duplicate cleanup behind it.
                var serviceShutdownResponse = await _vpnServiceClient.SendAsync(new VpnServiceRequest
                {
                    Command = VpnCommandType.ShutdownService
                }, timeoutCts.Token);

                if (!serviceShutdownResponse.Success || serviceShutdownResponse.TunnelActive)
                {
                    var message = BuildShutdownFailureMessage(serviceShutdownResponse);
                    _logger.LogWarning(message);
                    return VpnShutdownResult.Failure(message);
                }

                _shutdownCompleted = true;
                _logger.LogInformation("VPN shutdown disconnect completed, verified, and service shutdown requested.");
                return VpnShutdownResult.Success("VPN shutdown disconnect completed.");
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var message = $"VPN shutdown disconnect timed out after {_shutdownTimeout.TotalSeconds:F0} seconds.";
                _logger.LogWarning(message);
                return VpnShutdownResult.Failure(message);
            }
            catch (OperationCanceledException)
            {
                const string message = "VPN shutdown disconnect was canceled.";
                _logger.LogWarning(message);
                return VpnShutdownResult.Failure(message);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to disconnect VPN during application shutdown.", ex);
                return VpnShutdownResult.Failure(ex.Message);
            }
        }
        finally
        {
            _shutdownGate.Release();
        }
    }

    private static string BuildShutdownFailureMessage(VpnServiceResponse response)
    {
        var detail = string.Join(Environment.NewLine,
            new[] { response.ErrorMessage, response.TunnelStatus, response.Output }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(detail)
            ? "VPN service did not verify tunnel teardown and accept shutdown."
            : detail;
    }
}

public sealed record VpnShutdownResult(bool Succeeded, string? Message)
{
    public static VpnShutdownResult Success(string? message = null) => new(true, message);
    public static VpnShutdownResult Failure(string message) => new(false, message);
}

public sealed record VpnTunnelStatus(
    bool IsActive,
    bool IsUnknown,
    bool OpenVpnActive,
    bool IkeV2Active,
    string? Detail)
{
    public bool ShouldWarnOnExit => IsActive || IsUnknown;

    public static VpnTunnelStatus Unknown(string detail, bool localActive) =>
        new(localActive, true, false, false, detail);
}
