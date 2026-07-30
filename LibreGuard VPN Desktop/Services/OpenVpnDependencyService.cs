using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

internal interface IOpenVpnDependencyService
{
    Task EnsureReadyAsync(VpnProtocol protocol, CancellationToken ct = default);
}

internal sealed class OpenVpnDependencyService : IOpenVpnDependencyService
{
    private const int VerificationTimeoutSeconds = 20;
    private static readonly TimeSpan VerificationPollDelay = TimeSpan.FromMilliseconds(500);

    private readonly IVpnServiceClient _vpnServiceClient;
    private readonly IOpenVpnSetupRunner _setupRunner;
    private readonly ILoggerService _logger;
    private readonly Func<string, bool> _confirmRepair;

    public OpenVpnDependencyService(IVpnServiceClient vpnServiceClient, ILoggerService logger)
        : this(vpnServiceClient, new ElevatedOpenVpnSetupRunner(), logger, ConfirmRepair)
    {
    }

    internal OpenVpnDependencyService(
        IVpnServiceClient vpnServiceClient,
        IOpenVpnSetupRunner setupRunner,
        ILoggerService logger,
        Func<string, bool> confirmRepair)
    {
        ArgumentNullException.ThrowIfNull(vpnServiceClient);
        ArgumentNullException.ThrowIfNull(setupRunner);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(confirmRepair);

        _vpnServiceClient = vpnServiceClient;
        _setupRunner = setupRunner;
        _logger = logger;
        _confirmRepair = confirmRepair;
    }

    public async Task EnsureReadyAsync(VpnProtocol protocol, CancellationToken ct = default)
    {
        if (protocol != VpnProtocol.OpenVPN)
            return;

        var health = await TryGetHealthAsync(ct);
        if (health.IsReady)
            return;

        var reason = health.SetupRequiredReason
            ?? "LibreGuard needs to install or repair OpenVPN before using this protocol.";

        if (!_confirmRepair(reason))
            throw new OpenVpnSetupException("OpenVPN setup was cancelled.");

        var request = OpenVpnSetupRequest.FromCurrentLayout();
        var result = await _setupRunner.RunRepairAsync(request, ct);
        if (!result.Success)
            throw new OpenVpnSetupException(BuildSetupFailureMessage(result));

        await VerifyReadyAfterRepairAsync(ct);
    }

    private async Task<OpenVpnHealthResult> TryGetHealthAsync(CancellationToken ct)
    {
        try
        {
            var response = await _vpnServiceClient.SendAsync(new VpnServiceRequest
            {
                Command = VpnCommandType.GetOpenVpnHealth
            }, ct);

            if (!response.Success)
            {
                if (response.ErrorMessage?.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return OpenVpnHealthResult.NeedsRepair(
                        "LibreGuard VPN Service needs to be updated before OpenVPN can start.");
                }

                return OpenVpnHealthResult.NeedsRepair(response.ErrorMessage);
            }

            return new OpenVpnHealthResult(
                IsReady: response.OpenVpnInstalled && response.OpenVpnDriverInstalled,
                SetupRequiredReason: response.SetupRequiredReason,
                OpenVpnExePath: response.OpenVpnExePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"OpenVPN health check failed: {ex.Message}");
            return OpenVpnHealthResult.NeedsRepair(
                "LibreGuard VPN Service is not installed or not reachable. LibreGuard needs to repair the service.");
        }
    }

    private async Task VerifyReadyAfterRepairAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(VerificationTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var health = await TryGetHealthAsync(ct);
            if (health.IsReady)
            {
                _logger.LogInformation($"OpenVPN setup verified. Executable: {health.OpenVpnExePath ?? "(unknown)"}");
                return;
            }

            await Task.Delay(VerificationPollDelay, ct);
        }

        throw new OpenVpnSetupException(
            "OpenVPN setup completed, but LibreGuard could not verify OpenVPN readiness. " +
            $"Check setup logs at {OpenVpnSetupPaths.LogDirectory}.");
    }

    private static string BuildSetupFailureMessage(OpenVpnSetupResult result)
    {
        var message = result.ExitCode switch
        {
            OpenVpnSetupExitCodes.CancelledOrNotElevated => "OpenVPN setup was cancelled or not approved.",
            OpenVpnSetupExitCodes.MsiFailed => "OpenVPN installer failed.",
            OpenVpnSetupExitCodes.ServiceStagingFailed => "LibreGuard VPN Service repair failed.",
            OpenVpnSetupExitCodes.VerificationFailed => "OpenVPN setup could not be verified.",
            _ => "OpenVPN setup failed."
        };

        if (!string.IsNullOrWhiteSpace(result.Message))
            message += $" {result.Message}";

        return $"{message} Check setup logs at {OpenVpnSetupPaths.LogDirectory}.";
    }

    private static bool ConfirmRepair(string reason)
    {
        var result = MessageBox.Show(
            $"{reason}\n\nLibreGuard will request administrator permission once to install or repair OpenVPN.",
            "OpenVPN Setup Required",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        return result == MessageBoxResult.OK;
    }
}

internal interface IOpenVpnSetupRunner
{
    Task<OpenVpnSetupResult> RunRepairAsync(OpenVpnSetupRequest request, CancellationToken ct);
}

internal sealed class ElevatedOpenVpnSetupRunner : IOpenVpnSetupRunner
{
    public async Task<OpenVpnSetupResult> RunRepairAsync(OpenVpnSetupRequest request, CancellationToken ct)
    {
        if (!File.Exists(request.HelperExePath))
        {
            return new OpenVpnSetupResult(
                OpenVpnSetupExitCodes.VerificationFailed,
                $"Setup helper not found at {request.HelperExePath}");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.HelperExePath,
                Arguments = $"repair-openvpn --install-root {Quote(request.InstallRoot)}",
                WorkingDirectory = Path.GetDirectoryName(request.HelperExePath) ?? request.InstallRoot,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new OpenVpnSetupResult(
                    OpenVpnSetupExitCodes.CancelledOrNotElevated,
                    "Setup helper did not start.");
            }

            await process.WaitForExitAsync(ct);
            return new OpenVpnSetupResult(process.ExitCode, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new OpenVpnSetupResult(
                OpenVpnSetupExitCodes.CancelledOrNotElevated,
                "Administrator permission was not approved.");
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

internal sealed record OpenVpnSetupRequest(string InstallRoot, string HelperExePath)
{
    public static OpenVpnSetupRequest FromCurrentLayout()
    {
        var appBase = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return FromAppBaseDirectory(appBase);
    }

    internal static OpenVpnSetupRequest FromAppBaseDirectory(string appBase)
    {
        appBase = Path.GetFullPath(appBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parent = Directory.GetParent(appBase)?.FullName ?? appBase;
        var installRoot = ResolveInstallRoot(appBase, parent);
        var helperExePath = Path.Combine(appBase, "setup", "LibreGuard.SetupHelper.exe");
        return new OpenVpnSetupRequest(installRoot, helperExePath);
    }

    private static string ResolveInstallRoot(string appBase, string parent)
    {
        foreach (var candidate in new[] { parent, appBase })
        {
            if (HasOpenVpnInstallerPayload(candidate))
                return candidate;
        }

        foreach (var candidate in new[] { parent, appBase })
        {
            if (Directory.Exists(Path.Combine(candidate, "service")))
                return candidate;
        }

        return appBase;
    }

    private static bool HasOpenVpnInstallerPayload(string root) =>
        File.Exists(Path.Combine(root, "installers", "openvpn", "OpenVPN-Community-amd64.msi"));
}

internal sealed record OpenVpnSetupResult(int ExitCode, string? Message)
{
    public bool Success => ExitCode == OpenVpnSetupExitCodes.Success;
}

internal static class OpenVpnSetupExitCodes
{
    public const int Success = 0;
    public const int CancelledOrNotElevated = 1;
    public const int MsiFailed = 2;
    public const int ServiceStagingFailed = 3;
    public const int VerificationFailed = 4;
}

internal static class OpenVpnSetupPaths
{
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LibreGuard VPN",
        "Logs");
}

internal sealed class OpenVpnSetupException : InvalidOperationException
{
    public OpenVpnSetupException(string message) : base(message)
    {
    }
}

internal sealed record OpenVpnHealthResult(bool IsReady, string? SetupRequiredReason, string? OpenVpnExePath)
{
    public static OpenVpnHealthResult NeedsRepair(string? reason) =>
        new(false, reason, null);
}
