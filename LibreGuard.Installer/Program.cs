using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using LibreGuard.Common.Windows;
using LibreGuard.Installer;

const string AppName = "LibreGuard VPN";
const string AppVersion = "1.1.1";
const string AppSupportUrl = "https://libreguard.net/Support";
const string AppExeName = "LibreGuard VPN Desktop.exe";
const string AppIconFileName = "LibreGuard_logo_cropped_V3.ico";
const string ServiceName = "LibreGuardVpnService";
const string DisplayName = "LibreGuard VPN Service";
const string Description = "Handles privileged VPN operations for the LibreGuard VPN Desktop app.";
const string OpenVpnInstallerRelativePath = "installers\\openvpn\\OpenVPN-Community-amd64.msi";
const string OpenVpnManifestRelativePath = "installers\\openvpn\\manifest.json";
const string WebView2BootstrapperRelativePath = "installers\\webview2\\MicrosoftEdgeWebView2Setup.exe";
const string WebView2ManifestRelativePath = "installers\\webview2\\manifest.json";
const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
const string UserDataDirectoryName = "LibreGuardVPN";

var context = InstallerContext.Create();

try
{
    var options = InstallerOptions.Parse(args);

    if (options.ShowHelp)
    {
        PrintUsage();
        return InstallerExitCodes.Success;
    }

    if (!IsAdministrator())
        return await CompleteAsync(InstallerExitCodes.CancelledOrNotElevated, "LibreGuard installer must run as administrator.");

    return options.Action switch
    {
        InstallerAction.Install => await InstallAsync(options, context),
        InstallerAction.Uninstall => await RunUninstallAsync(options, context),
        _ => await CompleteAsync(InstallerExitCodes.VerificationFailed, "Unknown installer action.")
    };
}
catch (Exception ex)
{
    context.Log(ex.ToString());
    return await CompleteAsync(InstallerExitCodes.VerificationFailed, ex.Message);
}

async Task<int> InstallAsync(InstallerOptions options, InstallerContext context)
{
    var bundleRoot = options.InstallRoot ?? ResolveBundleRoot();
    context.Log($"Bundle root: {bundleRoot}");

    ValidateBundleLayout(bundleRoot);
    ValidateBundleContainsExpectedUiMarkers(bundleRoot, context);

    var installerPath = Path.Combine(bundleRoot, OpenVpnInstallerRelativePath);
    if (!File.Exists(installerPath))
        return await CompleteAsync(InstallerExitCodes.MsiFailed, $"OpenVPN installer not found: {installerPath}");

    ValidateOpenVpnChecksumIfConfigured(bundleRoot, installerPath, context);

    var webView2ExitCode = await EnsureWebView2RuntimeAsync(bundleRoot, context);
    if (webView2ExitCode != 0)
        return await CompleteAsync(InstallerExitCodes.VerificationFailed, $"Microsoft Edge WebView2 Runtime installation failed with exit code {webView2ExitCode}.");

    var installDir = options.TargetDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        AppName);
    var appInstallDir = Path.Combine(installDir, "app");
    var setupInstallDir = Path.Combine(appInstallDir, "setup");
    var serviceInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppName,
        "Service");
    var userDataDir = GetUserDataDirectory();

    context.Log($"Install dir: {installDir}");

    await StopServiceIfExistsAsync(context);

    if (options.ClearUserData)
        DeleteUserDataIfExists(userDataDir, context, allowPartialCleanup: true);

    var msiLogPath = Path.Combine(context.LogDirectory, "openvpn-msi.log");
    var msiExitCode = await InstallOpenVpnAsync(installerPath, msiLogPath, context);

    if (msiExitCode is not (0 or 3010))
        return await CompleteAsync(InstallerExitCodes.MsiFailed, $"OpenVPN MSI failed with exit code {msiExitCode}.");

    RemoveOpenVpnShellArtifacts(context);
    HideOpenVpnUninstallEntries(context);

    try
    {
        MirrorDirectory(Path.Combine(bundleRoot, "app"), appInstallDir, context);
        MirrorDirectory(Path.Combine(bundleRoot, "licenses"), Path.Combine(installDir, "licenses"), context);
        MirrorDirectory(Path.Combine(bundleRoot, "service"), serviceInstallDir, context);
        MirrorDirectory(Path.Combine(bundleRoot, "service"), Path.Combine(installDir, "service"), context);
        MirrorDirectory(Path.Combine(bundleRoot, "installers"), Path.Combine(installDir, "installers"), context);
        MirrorDirectory(AppContext.BaseDirectory, Path.Combine(installDir, "installer"), context);
        CopyAppIcon(appInstallDir, serviceInstallDir, context);

        var openVpnBin = ResolveOpenVpnBinDirectory();
        if (openVpnBin is null)
            return await CompleteAsync(InstallerExitCodes.VerificationFailed, "OpenVPN bin directory was not found after MSI installation.");

        MirrorDirectory(openVpnBin, Path.Combine(serviceInstallDir, "bin"), context);
        CopyFallbackSetupHelper(bundleRoot, setupInstallDir, context);
    }
    catch (Exception ex)
    {
        context.Log(ex.ToString());
        return await CompleteAsync(InstallerExitCodes.ServiceStagingFailed, ex.Message);
    }

    var serviceExePath = Path.Combine(serviceInstallDir, "LibreGuard.VpnService.exe");
    if (!File.Exists(serviceExePath))
        return await CompleteAsync(InstallerExitCodes.ServiceStagingFailed, $"Staged service executable not found: {serviceExePath}");

    await CreateOrUpdateServiceAsync(serviceExePath, context);
    if (options.CreateShortcuts)
        CreateStartMenuShortcut(Path.Combine(appInstallDir, AppExeName), context);
    WriteUninstallRegistry(installDir, context);

    var appExePath = Path.Combine(appInstallDir, AppExeName);
    if (!File.Exists(appExePath))
        return await CompleteAsync(InstallerExitCodes.VerificationFailed, $"Installed app executable not found: {appExePath}");

    var bundledOpenVpnExe = Path.Combine(serviceInstallDir, "bin", "openvpn.exe");
    if (!File.Exists(bundledOpenVpnExe))
        return await CompleteAsync(InstallerExitCodes.VerificationFailed, $"Bundled openvpn.exe not found: {bundledOpenVpnExe}");

    return await CompleteAsync(InstallerExitCodes.Success, "LibreGuard VPN installation completed.");
}

async Task<int> RunUninstallAsync(InstallerOptions options, InstallerContext context)
{
    if (options.Quiet || options.CleanupWorker)
        return await UninstallAsync(options, context, removeUserData: options.ClearUserData);

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

    using var dialog = new UninstallDialog(async removeUserData => await UninstallAsync(options, context, removeUserData));
    var result = dialog.ShowDialog();

    return result == DialogResult.OK
        ? dialog.ExitCode ?? InstallerExitCodes.Success
        : await CompleteAsync(InstallerExitCodes.CancelledOrNotElevated, "LibreGuard VPN uninstall cancelled.");
}

async Task<int> UninstallAsync(InstallerOptions options, InstallerContext context, bool removeUserData)
{
    var installDir = ResolveUninstallInstallDir(options);
    var userDataDir = GetUserDataDirectory();

    if (!options.CleanupWorker && IsRunningFromInstalledInstaller(installDir))
        return await LaunchUninstallWorkerAsync(installDir, removeUserData, context);

    if (options.CleanupWorker && options.WaitForProcessId is int waitForProcessId)
        await WaitForProcessToExitAsync(waitForProcessId, context);

    await TerminateAppProcessesAsync(installDir, context);

    TryDeleteShortcutArtifacts(installDir, context);

    await StopServiceIfExistsAsync(context);
    await RunProcessAsync("sc.exe", ["delete", ServiceName], context, allowFailure: true);
    await UninstallOpenVpnIfPresentAsync(context);

    DeleteDirectoryIfExists(installDir, context);
    DeleteDirectoryIfExists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppName,
        "Service"), context);
    if (removeUserData)
        DeleteUserDataIfExists(userDataDir, context, allowPartialCleanup: true);
    DeleteStartMenuShortcut(context);
    DeleteDesktopShortcut(context);
    DeleteUninstallRegistry(context);

    return await CompleteAsync(InstallerExitCodes.Success, "LibreGuard VPN uninstalled.");
}

static string ResolveUninstallInstallDir(InstallerOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.TargetDir))
        return Path.GetFullPath(options.TargetDir);

    var baseDir = Path.GetFullPath(AppContext.BaseDirectory)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var parentDir = Directory.GetParent(baseDir)?.FullName;
    if (!string.IsNullOrWhiteSpace(parentDir) &&
        string.Equals(Path.GetFileName(parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), AppName, StringComparison.OrdinalIgnoreCase))
    {
        return parentDir;
    }

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        AppName);
}

async Task<int> CompleteAsync(int exitCode, string message)
{
    context.Log(message);
    await context.WriteStatusAsync(exitCode, message);
    return exitCode;
}

static void PrintUsage()
{
    Console.WriteLine("""
        LibreGuard.Installer install [--quiet] [--clear-user-data] [--install-root <bundle-root>] [--target-dir <path>]
        LibreGuard.Installer uninstall [--quiet] [--clear-user-data] [--cleanup-worker] [--wait-for-pid <pid>] [--target-dir <path>]

        Store/automation silent install example:
          LibreGuard.Installer.exe install --quiet
        """);
}

static void ValidateBundleLayout(string bundleRoot)
{
    foreach (var required in new[] { "app", "service", "installers", "licenses" })
    {
        var path = Path.Combine(bundleRoot, required);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Required bundle folder not found: {path}");
    }
}

static void ValidateBundleContainsExpectedUiMarkers(string bundleRoot, InstallerContext context)
{
    var appDllPath = Path.Combine(bundleRoot, "app", "LibreGuard VPN Desktop.dll");
    if (!File.Exists(appDllPath))
        throw new FileNotFoundException($"Published desktop app assembly not found: {appDllPath}");

    var dllText = Encoding.Latin1.GetString(File.ReadAllBytes(appDllPath));
    var requiredMarkers = new[] { "GroupedServers", "ServerSummary", "DataUsageProgressStyle", "LoadToBrush" };
    foreach (var marker in requiredMarkers)
    {
        if (!dllText.Contains(marker, StringComparison.Ordinal))
        {
            context.Log($"App bundle marker missing: {marker}");
            throw new InvalidOperationException(
                "The bundle appears stale. Re-run scripts/publish-vm-bundle.ps1 and install from the fresh publish\\release-bundle folder.");
        }
    }

    context.Log("Published app bundle markers verified.");
}

static string ResolveBundleRoot()
{
    var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var parent = Directory.GetParent(baseDir)?.FullName ?? baseDir;
    return Directory.Exists(Path.Combine(parent, "app")) ? parent : baseDir;
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static void ValidateOpenVpnChecksumIfConfigured(string bundleRoot, string installerPath, InstallerContext context)
{
    var manifestPath = Path.Combine(bundleRoot, OpenVpnManifestRelativePath);
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

static async Task<int> EnsureWebView2RuntimeAsync(string bundleRoot, InstallerContext context)
{
    if (IsWebView2RuntimeInstalled(context))
    {
        context.Log("Microsoft Edge WebView2 Runtime is already installed.");
        return 0;
    }

    var bootstrapperPath = Path.Combine(bundleRoot, WebView2BootstrapperRelativePath);
    if (!File.Exists(bootstrapperPath))
    {
        context.Log($"WebView2 bootstrapper not found: {bootstrapperPath}");
        return InstallerExitCodes.VerificationFailed;
    }

    ValidateWebView2BootstrapperChecksum(bundleRoot, bootstrapperPath);
    context.Log("Installing Microsoft Edge WebView2 Evergreen Runtime.");
    var exitCode = await RunProcessAsync(
        bootstrapperPath,
        ["/silent", "/install"],
        context);

    if (exitCode != 0)
        return exitCode;

    if (!IsWebView2RuntimeInstalled(context))
    {
        context.Log("WebView2 bootstrapper completed, but the runtime could not be detected.");
        return InstallerExitCodes.VerificationFailed;
    }

    return 0;
}

static bool IsWebView2RuntimeInstalled(InstallerContext context)
{
    foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
    {
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var clientKey = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2ClientId}");
                var version = clientKey?.GetValue("pv") as string;
                if (!string.IsNullOrWhiteSpace(version) && version != "0.0.0.0")
                {
                    context.Log($"Detected WebView2 Runtime {version} in {hive} {view}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                context.Log($"Could not inspect WebView2 runtime registration in {hive} {view}: {ex.Message}");
            }
        }
    }

    return false;
}

static void ValidateWebView2BootstrapperChecksum(string bundleRoot, string bootstrapperPath)
{
    var manifestPath = Path.Combine(bundleRoot, WebView2ManifestRelativePath);
    if (!File.Exists(manifestPath))
        throw new FileNotFoundException("WebView2 bootstrapper manifest was not found.", manifestPath);

    var manifest = JsonSerializer.Deserialize<WebView2BootstrapperManifest>(
        File.ReadAllText(manifestPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    var expectedSha256 = manifest?.Sha256?.Trim();
    if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Contains("replace", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("WebView2 bootstrapper manifest does not contain a final SHA-256 checksum.");

    using var stream = File.OpenRead(bootstrapperPath);
    var actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
    if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("WebView2 bootstrapper SHA-256 does not match its manifest.");
}

static Task<int> InstallOpenVpnAsync(string installerPath, string logPath, InstallerContext context)
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

static async Task UninstallOpenVpnIfPresentAsync(InstallerContext context)
{
    var productCode = TryFindOpenVpnProductCode(context);
    if (string.IsNullOrWhiteSpace(productCode))
    {
        context.Log("OpenVPN uninstall product code not found; skipping MSI uninstall.");
        RemoveOpenVpnShellArtifacts(context);
        return;
    }

    var uninstallLogPath = Path.Combine(context.LogDirectory, "openvpn-msi-uninstall.log");
    await RunProcessAsync(
        "msiexec.exe",
        ["/x", productCode, "/qn", "/norestart", "/L*v", uninstallLogPath],
        context,
        allowFailure: true);

    RemoveOpenVpnShellArtifacts(context);
}

static void HideOpenVpnUninstallEntries(InstallerContext context)
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

static string? TryFindOpenVpnProductCode(InstallerContext context)
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
                using var productKey = uninstallKey.OpenSubKey(subKeyName);
                if (productKey is null)
                    continue;

                var displayName = productKey.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName) ||
                    displayName.IndexOf("OpenVPN", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var uninstallString = productKey.GetValue("QuietUninstallString") as string ??
                                      productKey.GetValue("UninstallString") as string;
                var productCode = TryExtractProductCode(subKeyName) ?? TryExtractProductCode(uninstallString);
                if (!string.IsNullOrWhiteSpace(productCode))
                {
                    context.Log($"Found OpenVPN MSI product code '{productCode}' in {view} uninstall registry.");
                    return productCode;
                }
            }
        }
        catch (Exception ex)
        {
            context.Log($"Failed to inspect OpenVPN uninstall registry in {view}: {ex.Message}");
        }
    }

    return null;
}

static string? TryExtractProductCode(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;

    var match = Regex.Match(value, "\\{[0-9A-Fa-f-]{36}\\}", RegexOptions.CultureInvariant);
    return match.Success ? match.Value : null;
}

static void RemoveOpenVpnShellArtifacts(InstallerContext context)
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

static void RemoveOpenVpnStartupEntries(InstallerContext context)
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

static void TerminateOpenVpnGuiProcesses(InstallerContext context)
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

static async Task StopServiceIfExistsAsync(InstallerContext context)
{
    if (!await ServiceExistsAsync(context))
        return;

    await RunProcessAsync("sc.exe", ["stop", ServiceName], context, allowFailure: true);
    await Task.Delay(TimeSpan.FromSeconds(2));
}

static async Task<bool> ServiceExistsAsync(InstallerContext context)
{
    var exitCode = await RunProcessAsync("sc.exe", ["query", ServiceName], context, allowFailure: true);
    return exitCode == 0;
}

static async Task CreateOrUpdateServiceAsync(string serviceExePath, InstallerContext context)
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

static Task GrantInteractiveServiceControlRightsAsync(InstallerContext context)
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

static void MirrorDirectory(string sourceDir, string destinationDir, InstallerContext context)
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

static void CopyFallbackSetupHelper(string bundleRoot, string setupInstallDir, InstallerContext context)
{
    var sourceSetupDir = Path.Combine(bundleRoot, "app", "setup");
    if (!Directory.Exists(sourceSetupDir))
        return;

    MirrorDirectory(sourceSetupDir, setupInstallDir, context);
}

static void DeleteDirectoryIfExists(string path, InstallerContext context)
{
    if (!Directory.Exists(path))
        return;

    for (var attempt = 1; attempt <= 5; attempt++)
    {
        try
        {
            context.Log($"Deleting '{path}' (attempt {attempt}/5)");
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(path, recursive: true);
            return;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
        {
            context.Log($"Retrying delete for '{path}': {ex.Message}");
            Thread.Sleep(500);
        }
    }

    context.Log($"Deleting '{path}' (final attempt)");
    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);

    Directory.Delete(path, recursive: true);
}

static bool IsRunningFromInstalledInstaller(string installDir)
{
    var baseDir = Path.GetFullPath(AppContext.BaseDirectory)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var installerDir = Path.Combine(Path.GetFullPath(installDir), "installer")
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    return baseDir.Equals(installerDir, StringComparison.OrdinalIgnoreCase) ||
           baseDir.StartsWith(installerDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

async Task<int> LaunchUninstallWorkerAsync(string installDir, bool removeUserData, InstallerContext context)
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "LibreGuard VPN", "Uninstall", Guid.NewGuid().ToString("N"));
    var tempInstallerDir = Path.Combine(tempRoot, "installer");
    Directory.CreateDirectory(tempInstallerDir);

    context.Log($"Staging uninstall worker to '{tempInstallerDir}'");
    MirrorDirectory(AppContext.BaseDirectory, tempInstallerDir, context);

    var workerExePath = Path.Combine(tempInstallerDir, "LibreGuard.Installer.exe");
    if (!File.Exists(workerExePath))
        return await CompleteAsync(InstallerExitCodes.VerificationFailed, $"Uninstall worker was not staged correctly: {workerExePath}");

    var workerStartInfo = new ProcessStartInfo
    {
        FileName = workerExePath,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = tempInstallerDir
    };
    workerStartInfo.ArgumentList.Add("uninstall");
    workerStartInfo.ArgumentList.Add("--quiet");
    workerStartInfo.ArgumentList.Add("--cleanup-worker");
    workerStartInfo.ArgumentList.Add("--wait-for-pid");
    workerStartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
    workerStartInfo.ArgumentList.Add("--target-dir");
    workerStartInfo.ArgumentList.Add(installDir);
    if (removeUserData)
        workerStartInfo.ArgumentList.Add("--clear-user-data");

    using var workerProcess = new Process { StartInfo = workerStartInfo };
    if (!workerProcess.Start())
        return await CompleteAsync(InstallerExitCodes.VerificationFailed, "Failed to launch uninstall worker.");

    context.Log($"Launched uninstall worker (PID {workerProcess.Id}).");
    return InstallerExitCodes.Success;
}

static async Task WaitForProcessToExitAsync(int processId, InstallerContext context)
{
    if (processId <= 0 || processId == Environment.ProcessId)
        return;

    try
    {
        using var process = Process.GetProcessById(processId);
        context.Log($"Waiting for process {processId} to exit before cleanup.");
        await process.WaitForExitAsync();
        await Task.Delay(750);
    }
    catch (ArgumentException)
    {
        context.Log($"Process {processId} already exited before cleanup started.");
    }
    catch (Exception ex)
    {
        context.Log($"Failed to wait for process {processId}: {ex.Message}");
    }
}

static async Task TerminateAppProcessesAsync(string installDir, InstallerContext context)
{
    var appExePath = Path.GetFullPath(Path.Combine(installDir, "app", AppExeName));
    var processName = Path.GetFileNameWithoutExtension(AppExeName);

    foreach (var process in Process.GetProcessesByName(processName))
    {
        try
        {
            var mainModulePath = string.Empty;
            try
            {
                mainModulePath = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                // Process metadata can be unavailable for elevated/system processes.
            }

            if (!string.IsNullOrWhiteSpace(mainModulePath) &&
                !string.Equals(Path.GetFullPath(mainModulePath), appExePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.Log($"Forcing LibreGuard app process {process.Id} to exit.");
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                process.Dispose();
            }
        }
        catch (Exception ex)
        {
            context.Log($"Failed to terminate LibreGuard process {process.Id}: {ex.Message}");
        }
    }

    await Task.Delay(1000);
}

static string GetUserDataDirectory() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    UserDataDirectoryName);

static void DeleteUserDataIfExists(string path, InstallerContext context, bool allowPartialCleanup)
{
    if (!Directory.Exists(path))
        return;

    context.Log($"Deleting user data folder: {path}");
    try
    {
        DeleteDirectoryIfExists(path, context);
    }
    catch (Exception ex)
    {
        context.Log($"Failed to delete user data folder: {ex.Message}");
        if (!allowPartialCleanup)
            throw;

        TryDeleteUserDataContents(path, context);
    }
}

static void TryDeleteUserDataContents(string path, InstallerContext context)
{
    if (!Directory.Exists(path))
        return;

    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
    {
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        catch (Exception ex)
        {
            context.Log($"Failed to delete user data file '{file}': {ex.Message}");
        }
    }

    foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
        .OrderByDescending(static directory => directory.Length))
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex)
        {
            context.Log($"Failed to delete user data directory '{directory}': {ex.Message}");
        }
    }

    try
    {
        Directory.Delete(path, recursive: false);
    }
    catch (Exception ex)
    {
        context.Log($"Leaving partial user data directory '{path}': {ex.Message}");
    }
}

static void CreateStartMenuShortcut(string appExePath, InstallerContext context)
{
    try
    {
        var shortcutIconPath = Path.Combine(Path.GetDirectoryName(appExePath)!, AppIconFileName);
        var startMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs",
            AppIdentity.ShortcutFolderName);
        Directory.CreateDirectory(startMenuDir);

        var shortcutPath = Path.Combine(startMenuDir, AppIdentity.ShortcutFileName);
        ShellLinkUtility.CreateOrUpdateShortcut(
            shortcutPath,
            appExePath,
            Path.GetDirectoryName(appExePath)!,
            File.Exists(shortcutIconPath) ? shortcutIconPath : appExePath,
            AppIdentity.AppUserModelId,
            AppIdentity.ToastActivatorClsid);
        context.Log($"Created shortcut: {shortcutPath}");
    }
    catch (Exception ex)
    {
        context.Log($"Failed to create shortcut: {ex.Message}");
    }
}

static void DeleteStartMenuShortcut(InstallerContext context)
{
    var startMenuDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        "Programs",
        AppIdentity.ShortcutFolderName);

    DeleteArtifactDirectoryIfExists(startMenuDir, context, "shortcut folder");
}

static void DeleteDesktopShortcut(InstallerContext context)
{
    DeleteShortcutIfExists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        $"{AppName}.lnk"), context, "shortcut");

    DeleteShortcutIfExists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        $"{AppName}.lnk"), context, "shortcut");
}

static void TryDeleteShortcutArtifacts(string installDir, InstallerContext context)
{
    var appIconPath = Path.Combine(installDir, "app", AppIconFileName);
    try
    {
        if (File.Exists(appIconPath))
        {
            File.SetAttributes(appIconPath, FileAttributes.Normal);
            File.Delete(appIconPath);
            context.Log($"Deleted installed app icon: {appIconPath}");
        }
    }
    catch (Exception ex)
    {
        context.Log($"Failed to delete installed app icon '{appIconPath}': {ex.Message}");
    }
}

static void DeleteShortcutIfExists(string shortcutPath, InstallerContext context, string artifactDescription)
{
    if (!File.Exists(shortcutPath))
        return;

    context.Log($"Deleting {artifactDescription}: {shortcutPath}");
    File.SetAttributes(shortcutPath, FileAttributes.Normal);
    File.Delete(shortcutPath);
}

static void DeleteArtifactDirectoryIfExists(string path, InstallerContext context, string artifactDescription)
{
    if (!Directory.Exists(path))
        return;

    context.Log($"Deleting {artifactDescription}: {path}");
    Directory.Delete(path, recursive: true);
}

static void CopyAppIcon(string appInstallDir, string serviceInstallDir, InstallerContext context)
{
    var sourceIconPath = Path.Combine(AppContext.BaseDirectory, AppIconFileName);
    if (!File.Exists(sourceIconPath))
    {
        context.Log($"App icon not found in installer payload: {sourceIconPath}");
        return;
    }

    foreach (var destinationDir in new[] { appInstallDir, serviceInstallDir })
    {
        try
        {
            Directory.CreateDirectory(destinationDir);
            var destinationPath = Path.Combine(destinationDir, AppIconFileName);
            File.Copy(sourceIconPath, destinationPath, overwrite: true);
            context.Log($"Copied app icon to: {destinationPath}");
        }
        catch (Exception ex)
        {
            context.Log($"Failed to copy app icon to '{destinationDir}': {ex.Message}");
        }
    }
}

static void WriteUninstallRegistry(string installDir, InstallerContext context)
{
    try
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LibreGuard VPN");

        var installerExe = Path.Combine(installDir, "installer", "LibreGuard.Installer.exe");
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", AppVersion);
        key.SetValue("Publisher", "LibreGuard d.o.o");
        key.SetValue("URLInfoAbout", AppSupportUrl);
        // Windows' legacy Programs and Features view falls back to URLInfoAbout
        // for the Help link when HelpLink is missing. Keep the value empty to
        // suppress that fallback while retaining the Support link above.
        key.SetValue("HelpLink", string.Empty, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", Path.Combine(installDir, "app", AppExeName));
        key.SetValue("UninstallString", $"\"{installerExe}\" uninstall");
        key.SetValue("QuietUninstallString", $"\"{installerExe}\" uninstall --quiet");
        key.SetValue("EstimatedSize", GetInstalledSizeInKilobytes(installDir), Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        context.Log("Wrote uninstall registry entry.");
    }
    catch (Exception ex)
    {
        context.Log($"Failed to write uninstall registry entry: {ex.Message}");
    }
}

static int GetInstalledSizeInKilobytes(string installDir)
{
    long totalBytes = 0;

    if (Directory.Exists(installDir))
    {
        foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                totalBytes += new FileInfo(file).Length;
            }
            catch (FileNotFoundException)
            {
                // A file removed during enumeration does not affect the estimate materially.
            }
            catch (DirectoryNotFoundException)
            {
                // A directory removed during enumeration does not affect the estimate materially.
            }
        }
    }

    var kilobytes = (totalBytes + 1023L) / 1024L;
    return (int)Math.Clamp(kilobytes, 1L, int.MaxValue);
}

static void DeleteUninstallRegistry(InstallerContext context)
{
    try
    {
        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LibreGuard VPN",
            throwOnMissingSubKey: false);
        context.Log("Deleted uninstall registry entry.");
    }
    catch (Exception ex)
    {
        context.Log($"Failed to delete uninstall registry entry: {ex.Message}");
    }
}

static async Task<int> RunProcessAsync(string fileName, string[] arguments, InstallerContext context, bool allowFailure = false)
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

internal enum InstallerAction
{
    Install,
    Uninstall
}

internal sealed record InstallerOptions(
    InstallerAction Action,
    bool Quiet,
    bool ShowHelp,
    bool CreateShortcuts,
    bool CleanupWorker,
    bool ClearUserData,
    int? WaitForProcessId,
    string? InstallRoot,
    string? TargetDir)
{
    public static InstallerOptions Parse(string[] args)
    {
        var action = InstallerAction.Install;
        var quiet = false;
        var showHelp = false;
        var createShortcuts = true;
        var cleanupWorker = false;
        var clearUserData = false;
        int? waitForProcessId = null;
        string? installRoot = null;
        string? targetDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "install", StringComparison.OrdinalIgnoreCase))
                action = InstallerAction.Install;
            else if (string.Equals(arg, "uninstall", StringComparison.OrdinalIgnoreCase))
                action = InstallerAction.Uninstall;
            else if (string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "/qn", StringComparison.OrdinalIgnoreCase))
                quiet = true;
            else if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase))
                showHelp = true;
            else if (string.Equals(arg, "--no-shortcuts", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--skip-shortcuts", StringComparison.OrdinalIgnoreCase))
                createShortcuts = false;
            else if (string.Equals(arg, "--cleanup-worker", StringComparison.OrdinalIgnoreCase))
                cleanupWorker = true;
            else if (string.Equals(arg, "--clear-user-data", StringComparison.OrdinalIgnoreCase))
                clearUserData = true;
            else if (string.Equals(arg, "--wait-for-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var parsedProcessId) && parsedProcessId > 0)
                    waitForProcessId = parsedProcessId;
            }
            else if (string.Equals(arg, "--install-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                installRoot = args[++i];
            else if (string.Equals(arg, "--target-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                targetDir = args[++i];
        }

        return new InstallerOptions(action, quiet, showHelp, createShortcuts, cleanupWorker, clearUserData, waitForProcessId, installRoot, targetDir);
    }
}

internal sealed class InstallerContext
{
    private InstallerContext(string logDirectory, string textLogPath, string statusPath)
    {
        LogDirectory = logDirectory;
        TextLogPath = textLogPath;
        StatusPath = statusPath;
    }

    public string LogDirectory { get; }
    public string TextLogPath { get; }
    public string StatusPath { get; }

    public static InstallerContext Create()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LibreGuard VPN",
            "Logs");

        Directory.CreateDirectory(logDirectory);
        return new InstallerContext(
            logDirectory,
            Path.Combine(logDirectory, "installer.log"),
            Path.Combine(logDirectory, "installer-status.json"));
    }

    public void Log(string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] {message}";
        File.AppendAllText(TextLogPath, line + Environment.NewLine);
        Console.WriteLine(line);
    }

    public Task WriteStatusAsync(int exitCode, string message)
    {
        var status = new InstallerStatus(
            UtcTimestamp: DateTimeOffset.UtcNow,
            ExitCode: exitCode,
            Success: exitCode == InstallerExitCodes.Success,
            Message: message,
            LogPath: TextLogPath);

        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(StatusPath, json);
    }
}

internal sealed record OpenVpnInstallerManifest(string? Version, string? FileName, string? Sha256, string? SourceUrl);
internal sealed record WebView2BootstrapperManifest(string? Version, string? FileName, string? Sha256, string? SourceUrl);

internal sealed record InstallerStatus(
    DateTimeOffset UtcTimestamp,
    int ExitCode,
    bool Success,
    string Message,
    string LogPath);

internal static class InstallerExitCodes
{
    public const int Success = 0;
    public const int CancelledOrNotElevated = 1;
    public const int MsiFailed = 2;
    public const int ServiceStagingFailed = 3;
    public const int VerificationFailed = 4;
}
