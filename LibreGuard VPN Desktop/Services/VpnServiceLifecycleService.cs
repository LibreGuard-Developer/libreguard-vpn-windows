using System.Diagnostics;

namespace LibreGuard_VPN_Desktop.Services;

internal sealed class VpnServiceLifecycleService : IVpnServiceLifecycleService
{
    private const string ServiceName = "LibreGuardVpnService";
    private static readonly TimeSpan ServiceStartTimeout = TimeSpan.FromSeconds(10);

    private readonly IVpnServiceClient _vpnServiceClient;
    private readonly ILoggerService _logger;

    public VpnServiceLifecycleService(IVpnServiceClient vpnServiceClient, ILoggerService logger)
    {
        ArgumentNullException.ThrowIfNull(vpnServiceClient);
        ArgumentNullException.ThrowIfNull(logger);

        _vpnServiceClient = vpnServiceClient;
        _logger = logger;
    }

    public async Task EnsureServiceRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await _vpnServiceClient.IsServiceAvailableAsync(cancellationToken))
            return;

        _logger.LogInformation("LibreGuard VPN Service is not reachable; attempting to start it.");

        try
        {
            await RunScAsync(["start", ServiceName], cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to start LibreGuard VPN Service: {ex.Message}");
        }

        var deadline = DateTime.UtcNow + ServiceStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _vpnServiceClient.IsServiceAvailableAsync(cancellationToken))
            {
                _logger.LogInformation("LibreGuard VPN Service is reachable.");
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        _logger.LogWarning("LibreGuard VPN Service did not become reachable after start attempt.");
    }

    private static async Task RunScAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        await process.WaitForExitAsync(cancellationToken);
    }
}
