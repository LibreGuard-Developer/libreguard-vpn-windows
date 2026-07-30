using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

const string ServiceName = "LibreGuardVpnService";
const string DisplayName = "LibreGuard VPN Service";
const string Description = "Handles privileged VPN operations for the LibreGuard VPN Desktop app.";
const string OpenVpnInstallerRelativePath = "installers\\openvpn\\OpenVPN-Community-amd64.msi";
const string OpenVpnManifestRelativePath = "installers\\openvpn\\manifest.json";

var context = SetupContext.Create();

try
{
    var command = args.FirstOrDefault();
    if (!string.Equals(command, "repair-openvpn", StringComparison.OrdinalIgnoreCase))
        return await CompleteAsync(OpenVpnSetupExitCodes.VerificationFailed, "Unknown setup command.");

    var installRoot = GetOption(args, "--install-root");
    if (string.IsNullOrWhiteSpace(installRoot))
        return await CompleteAsync(OpenVpnSetupExitCodes.VerificationFailed, "--install-root is required.");

    installRoot = Path.GetFullPath(installRoot);
    context.Log($"Install root: {installRoot}");

    if (!IsAdministrator())
        return await CompleteAsync(OpenVpnSetupExitCodes.CancelledOrNotElevated, "Setup helper is not elevated.");

    var installerPath = Path.Combine(installRoot, OpenVpnInstallerRelativePath);
    if (!File.Exists(installerPath))
        return await CompleteAsync(OpenVpnSetupExitCodes.MsiFailed, $"OpenVPN installer not found: {installerPath}");

    ValidateInstallerChecksumIfConfigured(installRoot, installerPath, context);

    var msiLogPath = Path.Combine(context.LogDirectory, "openvpn-msi.log");
    var msiExitCode = await InstallOpenVpnAsync(installerPath, msiLogPath, context);

    if (msiExitCode is not (0 or 3010))
        return await CompleteAsync(OpenVpnSetupExitCodes.MsiFailed, $"OpenVPN MSI failed with exit code {msiExitCode}.");

    RemoveOpenVpnShellArtifacts(context);
    HideOpenVpnUninstallEntries(context);

    var serviceSourceDir = Path.Combine(installRoot, "service");
    if (!Directory.Exists(serviceSourceDir))
        return await CompleteAsync(OpenVpnSetupExitCodes.ServiceStagingFailed, $"Service payload not found: {serviceSourceDir}");

    var serviceInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LibreGuard VPN",
        "Service");

    try
    {
        await StopServiceIfExistsAsync(context);
        MirrorDirectory(serviceSourceDir, serviceInstallDir, context);
    }
    catch (Exception ex)
    {
        context.Log(ex.ToString());
        return await CompleteAsync(OpenVpnSetupExitCodes.ServiceStagingFailed, ex.Message);
    }

    var openVpnBin = ResolveOpenVpnBinDirectory();
    if (openVpnBin is null)
        return await CompleteAsync(OpenVpnSetupExitCodes.VerificationFailed, "OpenVPN bin directory was not found after MSI installation.");

    MirrorDirectory(openVpnBin, Path.Combine(serviceInstallDir, "bin"), context);

    var serviceExePath = Path.Combine(serviceInstallDir, "LibreGuard.VpnService.exe");
    if (!File.Exists(serviceExePath))
        return await CompleteAsync(OpenVpnSetupExitCodes.ServiceStagingFailed, $"Staged service executable not found: {serviceExePath}");

    await CreateOrUpdateServiceAsync(serviceExePath, context);

    var bundledOpenVpnExe = Path.Combine(serviceInstallDir, "bin", "openvpn.exe");
    if (!File.Exists(bundledOpenVpnExe))
        return await CompleteAsync(OpenVpnSetupExitCodes.VerificationFailed, $"Bundled openvpn.exe not found: {bundledOpenVpnExe}");

    return await CompleteAsync(OpenVpnSetupExitCodes.Success, "OpenVPN setup completed.");
}
catch (Exception ex)
{
    context.Log(ex.ToString());
    return await CompleteAsync(OpenVpnSetupExitCodes.VerificationFailed, ex.Message);
}

async Task<int> CompleteAsync(int exitCode, string message)
{
    context.Log(message);
    await context.WriteStatusAsync(exitCode, message);
    return exitCode;
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static void ValidateInstallerChecksumIfConfigured(string installRoot, string installerPath, SetupContext context)
{
    var manifestPath = Path.Combine(installRoot, OpenVpnManifestRelativePath);
    if (!File.Exists(manifestPath))
    {
        context.Log("OpenVPN manifest not found; skipping checksum validation.");
        return;
    }

    var manifest = JsonSerializer.Deserialize<OpenVpnInstallerManifest>(
        File.ReadAllText(manifestPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    var expectedSha256 = manifest?.Sha256?.Trim();
    if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Contains("replace", StringComparison.OrdinalIgnoreCase))
    {
        context.Log("OpenVPN manifest does not contain a final SHA-256; skipping checksum validation.");
        return;
    }

    using var stream = File.OpenRead(installerPath);
    var actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
    if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("OpenVPN installer SHA-256 does not match manifest.");

    context.Log("OpenVPN installer checksum verified.");
}

static Task<int> InstallOpenVpnAsync(string installerPath, string logPath, SetupContext context)
{
    return RunProcessAsync(
        "msiexec.exe",
        [
            "/i",
            installerPath,
            "/qn",
            "/norestart",
            "ALLUSERS=1",
            "ADDLOCAL=OpenVPN,Drivers,Drivers.TAPWindows6",
            "REMOVE=OpenVPN.GUI,OpenVPN.GUI.OnLogon",
            "/L*v",
            logPath
        ],
        context);
}

static void RemoveOpenVpnShellArtifacts(SetupContext context)
{
    RemoveOpenVpnStartupEntries(context);
    TerminateOpenVpnGuiProcesses(context);

    foreach (var directory in GetOpenVpnShortcutDirectories())
        DeleteArtifactDirectoryIfExists(directory, context, "OpenVPN shortcut directory");

    foreach (var shortcutPath in GetOpenVpnShortcutPaths())
        DeleteShortcutIfExists(shortcutPath, context, "OpenVPN shortcut");

    foreach (var programsDirectory in GetProgramsDirectories())
    {
        if (!Directory.Exists(programsDirectory))
            continue;

        foreach (var shortcutPath in Directory.EnumerateFiles(programsDirectory, "*OpenVPN*.lnk", SearchOption.TopDirectoryOnly))
            DeleteShortcutIfExists(shortcutPath, context, "OpenVPN shortcut");
    }
}

static void RemoveOpenVpnStartupEntries(SetupContext context)
{
    foreach (var hive in new[] { Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryHive.LocalMachine })
    {
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var runKey = baseKey.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    writable: true);

                if (runKey?.GetValue("OpenVPN-GUI") is null)
                    continue;

                runKey.DeleteValue("OpenVPN-GUI", throwOnMissingValue: false);
                context.Log($"Deleted OpenVPN-GUI startup entry from {hive} {view}.");
            }
            catch (Exception ex)
            {
                context.Log($"Failed to delete OpenVPN-GUI startup entry from {hive} {view}: {ex.Message}");
            }
        }
    }
}

static void TerminateOpenVpnGuiProcesses(SetupContext context)
{
    foreach (var processName in new[] { "openvpn-gui", "openvpn_gui" })
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                context.Log($"Stopping OpenVPN GUI tray process {process.Id}.");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                context.Log($"Failed to stop OpenVPN GUI tray process {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

static void HideOpenVpnUninstallEntries(SetupContext context)
{
    foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstallKey is null)
                continue;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var productKey = uninstallKey.OpenSubKey(subKeyName, writable: true);
                if (productKey is null)
                    continue;

                var displayName = productKey.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName) ||
                    displayName.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                productKey.SetValue("SystemComponent", 1, Microsoft.Win32.RegistryValueKind.DWord);
                context.Log($"Marked OpenVPN uninstall entry '{subKeyName}' as hidden in {view} uninstall registry.");
            }
        }
        catch (Exception ex)
        {
            context.Log($"Failed to hide OpenVPN uninstall entries in {view}: {ex.Message}");
        }
    }
}

static IEnumerable<string> GetOpenVpnShortcutDirectories()
{
    foreach (var programsDirectory in GetProgramsDirectories())
        yield return Path.Combine(programsDirectory, "OpenVPN");
}

static IEnumerable<string> GetProgramsDirectories()
{
    yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
    yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
}

static IEnumerable<string> GetOpenVpnShortcutPaths()
{
    var fileNames = new[]
    {
        "OpenVPN GUI.lnk",
        "OpenVPN Connect.lnk"
    };

    foreach (var desktopDirectory in new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(static path => !string.IsNullOrWhiteSpace(path)))
    {
        foreach (var fileName in fileNames)
            yield return Path.Combine(desktopDirectory, fileName);
    }
}

static async Task StopServiceIfExistsAsync(SetupContext context)
{
    if (!await ServiceExistsAsync(context))
        return;

    await RunProcessAsync("sc.exe", ["stop", ServiceName], context, allowFailure: true);
    await Task.Delay(TimeSpan.FromSeconds(2));
}

static async Task<bool> ServiceExistsAsync(SetupContext context)
{
    var exitCode = await RunProcessAsync("sc.exe", ["query", ServiceName], context, allowFailure: true);
    return exitCode == 0;
}

static async Task CreateOrUpdateServiceAsync(string serviceExePath, SetupContext context)
{
    if (await ServiceExistsAsync(context))
    {
        var configExitCode = await RunProcessAsync(
            "sc.exe",
            ["config", ServiceName, "binPath=", serviceExePath, "start=", "auto", "DisplayName=", DisplayName],
            context);

        if (configExitCode != 0)
            throw new InvalidOperationException($"Failed to update service configuration (exit {configExitCode}).");
    }
    else
    {
        var createExitCode = await RunProcessAsync(
            "sc.exe",
            ["create", ServiceName, "binPath=", serviceExePath, "start=", "auto", "DisplayName=", DisplayName],
            context);

        if (createExitCode != 0)
            throw new InvalidOperationException($"Failed to create service (exit {createExitCode}).");
    }

    await RunProcessAsync("sc.exe", ["description", ServiceName, Description], context, allowFailure: true);
    await RunProcessAsync("sc.exe", ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000"], context, allowFailure: true);
    await GrantInteractiveServiceControlRightsAsync(context);

    var startExitCode = await RunProcessAsync("sc.exe", ["start", ServiceName], context, allowFailure: true);
    if (startExitCode != 0)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        startExitCode = await RunProcessAsync("sc.exe", ["query", ServiceName], context, allowFailure: true);
        if (startExitCode != 0)
            throw new InvalidOperationException($"Failed to start service (exit {startExitCode}).");
    }
}

static Task GrantInteractiveServiceControlRightsAsync(SetupContext context)
{
    // Allows authenticated desktop users to query, start, stop, and interrogate only this service.
    const string serviceSecurityDescriptor =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)" +
        "(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
        "(A;;CCLCSWLOCRRC;;;IU)" +
        "(A;;CCLCSWLOCRRC;;;SU)" +
        "(A;;LCRPWPLO;;;AU)";

    return RunProcessAsync("sc.exe", ["sdset", ServiceName, serviceSecurityDescriptor], context, allowFailure: true);
}

static string? ResolveOpenVpnBinDirectory()
{
    var candidates = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN", "bin")
    };

    return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "openvpn.exe")));
}

static void MirrorDirectory(string sourceDir, string destinationDir, SetupContext context)
{
    sourceDir = Path.GetFullPath(sourceDir);
    destinationDir = Path.GetFullPath(destinationDir);
    context.Log($"Mirroring '{sourceDir}' to '{destinationDir}'");

    Directory.CreateDirectory(destinationDir);

    foreach (var file in Directory.EnumerateFiles(destinationDir, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);

    foreach (var directory in Directory.EnumerateDirectories(destinationDir))
        Directory.Delete(directory, recursive: true);

    foreach (var file in Directory.EnumerateFiles(destinationDir))
        File.Delete(file);

    foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceDir, directory);
        Directory.CreateDirectory(Path.Combine(destinationDir, relative));
    }

    foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceDir, file);
        var target = Path.Combine(destinationDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static void DeleteShortcutIfExists(string shortcutPath, SetupContext context, string artifactDescription)
{
    if (!File.Exists(shortcutPath))
        return;

    context.Log($"Deleting {artifactDescription}: {shortcutPath}");
    File.SetAttributes(shortcutPath, FileAttributes.Normal);
    File.Delete(shortcutPath);
}

static void DeleteArtifactDirectoryIfExists(string path, SetupContext context, string artifactDescription)
{
    if (!Directory.Exists(path))
        return;

    context.Log($"Deleting {artifactDescription}: {path}");
    Directory.Delete(path, recursive: true);
}

static async Task<int> RunProcessAsync(string fileName, string[] arguments, SetupContext context, bool allowFailure = false)
{
    context.Log($"> {fileName} {string.Join(' ', arguments.Select(QuoteForLog))}");

    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);

    using var process = new Process { StartInfo = startInfo };
    process.Start();
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    var stdout = await stdoutTask;
    var stderr = await stderrTask;

    if (!string.IsNullOrWhiteSpace(stdout))
        context.Log(stdout.Trim());
    if (!string.IsNullOrWhiteSpace(stderr))
        context.Log(stderr.Trim());

    context.Log($"Exit code: {process.ExitCode}");

    if (!allowFailure && process.ExitCode != 0)
        context.Log($"Command failed: {fileName}");

    return process.ExitCode;
}

static string QuoteForLog(string value) =>
    value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

internal sealed class SetupContext
{
    private readonly List<string> _lines = [];

    private SetupContext(string logDirectory, string textLogPath, string statusPath)
    {
        LogDirectory = logDirectory;
        TextLogPath = textLogPath;
        StatusPath = statusPath;
    }

    public string LogDirectory { get; }
    public string TextLogPath { get; }
    public string StatusPath { get; }

    public static SetupContext Create()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LibreGuard VPN",
            "Logs");

        Directory.CreateDirectory(logDirectory);
        return new SetupContext(
            logDirectory,
            Path.Combine(logDirectory, "openvpn-setup.log"),
            Path.Combine(logDirectory, "openvpn-setup-status.json"));
    }

    public void Log(string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] {message}";
        _lines.Add(line);
        File.AppendAllText(TextLogPath, line + Environment.NewLine);
    }

    public Task WriteStatusAsync(int exitCode, string message)
    {
        var status = new OpenVpnSetupStatus(
            UtcTimestamp: DateTimeOffset.UtcNow,
            ExitCode: exitCode,
            Success: exitCode == OpenVpnSetupExitCodes.Success,
            Message: message,
            LogPath: TextLogPath);

        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(StatusPath, json);
    }
}

internal sealed record OpenVpnInstallerManifest(string? Version, string? FileName, string? Sha256, string? SourceUrl);

internal sealed record OpenVpnSetupStatus(
    DateTimeOffset UtcTimestamp,
    int ExitCode,
    bool Success,
    string Message,
    string LogPath);

internal static class OpenVpnSetupExitCodes
{
    public const int Success = 0;
    public const int CancelledOrNotElevated = 1;
    public const int MsiFailed = 2;
    public const int ServiceStagingFailed = 3;
    public const int VerificationFailed = 4;
}
