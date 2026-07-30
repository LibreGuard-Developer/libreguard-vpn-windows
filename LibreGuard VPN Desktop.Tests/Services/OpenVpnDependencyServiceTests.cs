using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;
using Xunit;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public class OpenVpnDependencyServiceTests
{
    [Fact]
    public void OpenVpnSetupRequest_FromInstalledAppLayout_UsesParentInstallRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"LibreGuardLayout-{Guid.NewGuid():N}");
        try
        {
            var appDir = Path.Combine(tempRoot, "app");
            var installerDir = Path.Combine(tempRoot, "installers", "openvpn");
            var serviceDir = Path.Combine(tempRoot, "service");
            Directory.CreateDirectory(Path.Combine(appDir, "setup"));
            Directory.CreateDirectory(installerDir);
            Directory.CreateDirectory(serviceDir);
            File.WriteAllText(Path.Combine(installerDir, "OpenVPN-Community-amd64.msi"), "");

            var request = OpenVpnSetupRequest.FromAppBaseDirectory(appDir);

            Assert.Equal(tempRoot, request.InstallRoot);
            Assert.Equal(Path.Combine(appDir, "setup", "LibreGuard.SetupHelper.exe"), request.HelperExePath);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureReadyAsync_Ikev2_DoesNotCheckHealthOrRunSetup()
    {
        var client = new SequencedVpnServiceClient();
        var runner = new RecordingOpenVpnSetupRunner();
        var service = CreateService(client, runner, confirmRepair: _ => true);

        await service.EnsureReadyAsync(VpnProtocol.IKEv2);

        Assert.Empty(client.Requests);
        Assert.False(runner.WasCalled);
    }

    [Fact]
    public async Task EnsureReadyAsync_OldServiceUnknownCommand_RunsRepairAndVerifies()
    {
        var client = new SequencedVpnServiceClient(
            new VpnServiceResponse
            {
                Success = false,
                ErrorMessage = "Unknown command: GetOpenVpnHealth"
            },
            new VpnServiceResponse
            {
                Success = true,
                OpenVpnInstalled = true,
                OpenVpnDriverInstalled = true,
                OpenVpnExePath = @"C:\ProgramData\LibreGuard VPN\Service\bin\openvpn.exe"
            });
        var runner = new RecordingOpenVpnSetupRunner();
        var service = CreateService(client, runner, confirmRepair: _ => true);

        await service.EnsureReadyAsync(VpnProtocol.OpenVPN);

        Assert.True(runner.WasCalled);
        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, request => Assert.Equal(VpnCommandType.GetOpenVpnHealth, request.Command));
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenUserCancels_ThrowsFriendlySetupException()
    {
        var client = new SequencedVpnServiceClient(new VpnServiceResponse
        {
            Success = true,
            OpenVpnInstalled = false,
            OpenVpnDriverInstalled = true,
            SetupRequiredReason = "OpenVPN is missing."
        });
        var runner = new RecordingOpenVpnSetupRunner();
        var service = CreateService(client, runner, confirmRepair: _ => false);

        var ex = await Assert.ThrowsAsync<OpenVpnSetupException>(
            () => service.EnsureReadyAsync(VpnProtocol.OpenVPN));

        Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(runner.WasCalled);
    }

    [Fact]
    public async Task EnsureReadyAsync_WhenElevatedHelperReturnsCancelled_ThrowsFriendlySetupException()
    {
        var client = new SequencedVpnServiceClient(new VpnServiceResponse
        {
            Success = true,
            OpenVpnInstalled = false,
            OpenVpnDriverInstalled = true,
            SetupRequiredReason = "OpenVPN is missing."
        });
        var runner = new RecordingOpenVpnSetupRunner
        {
            Result = new OpenVpnSetupResult(OpenVpnSetupExitCodes.CancelledOrNotElevated, "Administrator permission was not approved.")
        };
        var service = CreateService(client, runner, confirmRepair: _ => true);

        var ex = await Assert.ThrowsAsync<OpenVpnSetupException>(
            () => service.EnsureReadyAsync(VpnProtocol.OpenVPN));

        Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(runner.WasCalled);
    }

    private static OpenVpnDependencyService CreateService(
        IVpnServiceClient client,
        IOpenVpnSetupRunner runner,
        Func<string, bool> confirmRepair)
    {
        return new OpenVpnDependencyService(client, runner, new TestLogger(), confirmRepair);
    }

    private sealed class SequencedVpnServiceClient : IVpnServiceClient
    {
        private readonly Queue<VpnServiceResponse> _responses;

        public SequencedVpnServiceClient(params VpnServiceResponse[] responses)
        {
            _responses = new Queue<VpnServiceResponse>(responses);
        }

        public List<VpnServiceRequest> Requests { get; } = [];

        public Task<VpnServiceResponse> SendAsync(VpnServiceRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
                throw new InvalidOperationException("No fake response configured.");

            return Task.FromResult(_responses.Dequeue());
        }

        public Task<bool> IsServiceAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class RecordingOpenVpnSetupRunner : IOpenVpnSetupRunner
    {
        public bool WasCalled { get; private set; }
        public OpenVpnSetupResult Result { get; init; } = new(OpenVpnSetupExitCodes.Success, null);

        public Task<OpenVpnSetupResult> RunRepairAsync(OpenVpnSetupRequest request, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(Result);
        }
    }

    private sealed class TestLogger : ILoggerService
    {
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }
}
