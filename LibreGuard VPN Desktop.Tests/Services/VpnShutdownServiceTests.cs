using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class VpnShutdownServiceTests
{
    [Fact]
    public async Task DisconnectOnExitAsync_WhenTunnelIsActive_UsesOneAtomicServiceShutdownAndRunsOnce()
    {
        var vpnService = new RecordingVpnConnectionService(ConnectionStatus.Connected);
        var serviceClient = new RecordingVpnServiceClient();
        var logger = new RecordingLoggerService();
        var shutdown = new VpnShutdownService(vpnService, serviceClient, logger, TimeSpan.FromSeconds(1));

        var first = await shutdown.DisconnectOnExitAsync(ActiveTunnelStatus());
        var second = await shutdown.DisconnectOnExitAsync();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(0, vpnService.DisconnectCalls);
        Assert.Equal(
            [VpnCommandType.ShutdownService],
            serviceClient.Requests.Select(r => r.Command));
        Assert.Contains(logger.InformationMessages, m => m.Contains("verified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DisconnectOnExitAsync_WhenAlreadyVerifiedDisconnected_SkipsDisconnectAndStopsService()
    {
        var vpnService = new RecordingVpnConnectionService(ConnectionStatus.Disconnected);
        var serviceClient = new RecordingVpnServiceClient();
        var logger = new RecordingLoggerService();
        var shutdown = new VpnShutdownService(vpnService, serviceClient, logger, TimeSpan.FromSeconds(1));

        var result = await shutdown.DisconnectOnExitAsync(DisconnectedTunnelStatus());

        Assert.True(result.Succeeded);
        Assert.Equal(0, vpnService.DisconnectCalls);
        Assert.Equal(
            [VpnCommandType.ShutdownService],
            serviceClient.Requests.Select(r => r.Command));
        Assert.Contains(logger.InformationMessages, m => m.Contains("already verified disconnected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DisconnectOnExitAsync_WhenStatusCheckFindsDisconnected_SkipsDisconnectAndStopsService()
    {
        var vpnService = new RecordingVpnConnectionService(ConnectionStatus.Disconnected);
        var serviceClient = new RecordingVpnServiceClient();
        var shutdown = new VpnShutdownService(
            vpnService,
            serviceClient,
            new RecordingLoggerService(),
            TimeSpan.FromSeconds(1));

        var result = await shutdown.DisconnectOnExitAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(0, vpnService.DisconnectCalls);
        Assert.Equal(
            [VpnCommandType.GetTunnelStatus, VpnCommandType.ShutdownService],
            serviceClient.Requests.Select(r => r.Command));
    }

    [Fact]
    public async Task DisconnectOnExitAsync_WhenServiceCannotTearDownTunnel_ReturnsFailureAndAllowsRetry()
    {
        var vpnService = new RecordingVpnConnectionService(ConnectionStatus.Connected);
        var serviceClient = new RecordingVpnServiceClient
        {
            ShutdownResponse = new VpnServiceResponse
            {
                Success = false,
                TunnelActive = true,
                ErrorMessage = "still connected"
            }
        };
        var logger = new RecordingLoggerService();
        var shutdown = new VpnShutdownService(vpnService, serviceClient, logger, TimeSpan.FromSeconds(1));

        var first = await shutdown.DisconnectOnExitAsync(ActiveTunnelStatus());
        serviceClient.ShutdownResponse = new VpnServiceResponse { Success = true, TunnelActive = false };
        var second = await shutdown.DisconnectOnExitAsync(ActiveTunnelStatus());

        Assert.False(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(0, vpnService.DisconnectCalls);
        Assert.Equal(2, serviceClient.Requests.Count(r => r.Command == VpnCommandType.ShutdownService));
    }

    [Fact]
    public async Task DisconnectOnExitAsync_WhenAtomicServiceShutdownTimesOut_ReturnsFailure()
    {
        var vpnService = new RecordingVpnConnectionService(ConnectionStatus.Connected);
        var serviceClient = new RecordingVpnServiceClient
        {
            DelayShutdownUntilCanceled = true
        };
        var logger = new RecordingLoggerService();
        var shutdown = new VpnShutdownService(vpnService, serviceClient, logger, TimeSpan.FromMilliseconds(20));

        var result = await shutdown.DisconnectOnExitAsync(ActiveTunnelStatus());

        Assert.False(result.Succeeded);
        Assert.Equal(0, vpnService.DisconnectCalls);
        Assert.Contains(logger.WarningMessages, m => m.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetTunnelStatusAsync_WhenServiceUnavailable_ReturnsUnknown()
    {
        var vpnService = new RecordingVpnConnectionService(ConnectionStatus.Disconnected);
        var serviceClient = new RecordingVpnServiceClient
        {
            SendException = new InvalidOperationException("service unavailable")
        };
        var shutdown = new VpnShutdownService(vpnService, serviceClient, new RecordingLoggerService(), TimeSpan.FromSeconds(1));

        var status = await shutdown.GetTunnelStatusAsync();

        Assert.True(status.IsUnknown);
        Assert.True(status.ShouldWarnOnExit);
    }

    [Fact]
    public async Task GetTunnelStatusAsync_WhenLocalStateIsStaleButServiceReportsDisconnected_ReturnsInactive()
    {
        var vpnService = new RecordingVpnConnectionService(ConnectionStatus.Disconnecting);
        var serviceClient = new RecordingVpnServiceClient
        {
            StatusResponse = new VpnServiceResponse
            {
                Success = true,
                TunnelActive = false,
                OpenVpnActive = false,
                IkeV2Active = false,
                TunnelStatus = "OpenVPN=Disconnected; IKEv2=Disconnected"
            }
        };
        var shutdown = new VpnShutdownService(vpnService, serviceClient, new RecordingLoggerService(), TimeSpan.FromSeconds(1));

        var status = await shutdown.GetTunnelStatusAsync();

        Assert.False(status.IsActive);
        Assert.False(status.IsUnknown);
        Assert.False(status.ShouldWarnOnExit);
        Assert.Equal("OpenVPN=Disconnected; IKEv2=Disconnected", status.Detail);
    }

    private static VpnTunnelStatus ActiveTunnelStatus() =>
        new(true, false, true, false, "OpenVPN=Connected; IKEv2=Disconnected");

    private static VpnTunnelStatus DisconnectedTunnelStatus() =>
        new(false, false, false, false, "OpenVPN=Disconnected; IKEv2=Disconnected");

    private sealed class RecordingVpnConnectionService : IVpnConnectionService
    {
        public RecordingVpnConnectionService(ConnectionStatus status)
        {
            Status = status;
        }

        public int DisconnectCalls { get; private set; }

        public ConnectionStatus Status { get; private set; }
        public ConnectionStats? CurrentStats => null;
        public string? VpnIpAddress => null;
        public string? LastErrorMessage => null;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<ConnectionStats>? StatsUpdated;

        public Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default)
        {
            Status = ConnectionStatus.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            Status = ConnectionStatus.Disconnected;
            return Task.CompletedTask;
        }

        public void RaiseUnusedEvents()
        {
            StatusChanged?.Invoke(this, Status);
            ErrorOccurred?.Invoke(this, string.Empty);
            StatsUpdated?.Invoke(this, new ConnectionStats(0, 0, 0, TimeSpan.Zero));
        }
    }

    private sealed class RecordingVpnServiceClient : IVpnServiceClient
    {
        public List<VpnServiceRequest> Requests { get; } = [];
        public VpnServiceResponse ShutdownResponse { get; set; } = new() { Success = true, TunnelActive = false };
        public VpnServiceResponse StatusResponse { get; set; } = new() { Success = true, TunnelActive = false };
        public Exception? SendException { get; init; }
        public bool DelayShutdownUntilCanceled { get; init; }

        public Task<VpnServiceResponse> SendAsync(VpnServiceRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);

            if (SendException is not null)
                throw SendException;

            if (DelayShutdownUntilCanceled && request.Command == VpnCommandType.ShutdownService)
                return Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(_ => ShutdownResponse, ct);

            return Task.FromResult(request.Command switch
            {
                VpnCommandType.ShutdownService => ShutdownResponse,
                VpnCommandType.GetTunnelStatus => StatusResponse,
                _ => new VpnServiceResponse { Success = true }
            });
        }

        public Task<bool> IsServiceAvailableAsync(CancellationToken ct = default)
        {
            return Task.FromResult(SendException is null);
        }
    }

    private sealed class RecordingLoggerService : ILoggerService
    {
        public List<string> InformationMessages { get; } = [];
        public List<string> WarningMessages { get; } = [];
        public List<(string Message, Exception? Exception)> Errors { get; } = [];

        public void LogInformation(string message) => InformationMessages.Add(message);
        public void LogWarning(string message) => WarningMessages.Add(message);
        public void LogError(string message, Exception? ex = null) => Errors.Add((message, ex));
    }
}
