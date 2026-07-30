using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace LibreGuard.VpnService;

/// <summary>
/// Manages the openvpn.exe process lifecycle within the Windows Service (LocalSystem).
/// Handles process launch, management interface monitoring, state tracking, and graceful shutdown.
/// Singleton � survives individual named pipe requests so the tunnel persists across client calls.
/// </summary>
internal sealed class OpenVpnProcessManager : IDisposable
{
    /// <summary>
    /// Bundled openvpn.exe path relative to the service install directory.
    /// The installer places openvpn.exe in a "bin" folder next to the service exe.
    /// </summary>
    private static readonly string BundledOpenVpnPath =
        Path.Combine(AppContext.BaseDirectory, "bin", "openvpn.exe");

    /// <summary>
    /// Fallback: registry key where the OpenVPN Community installer stores its path.
    /// </summary>
    private const string OpenVpnRegistryKey = @"SOFTWARE\OpenVPN";

    /// <summary>
    /// Registry class key for network adapter enumeration (TAP/Wintun driver detection).
    /// </summary>
    private const string NetworkAdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    /// <summary>
    /// Known TAP/TUN driver component IDs.
    /// </summary>
    private static readonly string[] TapComponentIds = ["tap0901", "tapwindows", "ovpn-dco", "win-dco", "wintun"];

    private const int ManagementPort = 7505;
    private const int ManagementConnectRetries = 10;
    private const int ManagementConnectDelayMs = 500;
    private const int ConnectionTimeoutSeconds = 30;
    private const int GracefulShutdownMs = 3000;
    private const int ForceKillMs = 5000;

    private const int MaxOutputLines = 50;

    private readonly ILogger<OpenVpnProcessManager> _logger;
    private readonly object _lock = new();
    private readonly Queue<string> _recentOutput = new();

    private Process? _process;
    private TcpClient? _managementClient;
    private StreamReader? _managementReader;
    private StreamWriter? _managementWriter;
    private CancellationTokenSource? _monitorCts;

    private volatile OpenVpnState _state = OpenVpnState.Disconnected;
    private volatile string? _lastError;
    private volatile string? _currentPassphrase;
    private long _bytesIn;
    private long _bytesOut;
    private volatile string? _localIp;

    public OpenVpnProcessManager(ILogger<OpenVpnProcessManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Current connection state, safe to read from any thread.
    /// </summary>
    public OpenVpnState State => _state;

    /// <summary>
    /// Last error or status detail from OpenVPN. Includes process output on failure.
    /// </summary>
    public string? LastError => _lastError;

    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public string? LocalIp => _localIp;
    public bool IsActive
    {
        get
        {
            Process? process;
            lock (_lock) { process = _process; }

            return _state is not OpenVpnState.Disconnected ||
                   process is { HasExited: false };
        }
    }

    public OpenVpnHealthSnapshot GetHealth()
    {
        return GetHealthCore(ResolveOpenVpnPath, IsTapDriverInstalled);
    }

    internal static OpenVpnHealthSnapshot GetHealthCore(Func<string?> resolveOpenVpnPath, Func<bool> isDriverInstalled)
    {
        ArgumentNullException.ThrowIfNull(resolveOpenVpnPath);
        ArgumentNullException.ThrowIfNull(isDriverInstalled);

        var openVpnExePath = resolveOpenVpnPath();
        var openVpnInstalled = !string.IsNullOrWhiteSpace(openVpnExePath);
        var driverInstalled = isDriverInstalled();

        var setupRequiredReason = (openVpnInstalled, driverInstalled) switch
        {
            (false, _) => "OpenVPN is not installed. LibreGuard needs to install OpenVPN before using this protocol.",
            (_, false) => "TAP/Wintun network adapter not found. LibreGuard needs to repair the OpenVPN driver.",
            _ => null
        };

        return new OpenVpnHealthSnapshot(
            OpenVpnInstalled: openVpnInstalled,
            OpenVpnExePath: openVpnExePath,
            OpenVpnDriverInstalled: driverInstalled,
            SetupRequiredReason: setupRequiredReason);
    }

    /// <summary>
    /// Returns the most recent OpenVPN output lines (up to <see cref="MaxOutputLines"/>).
    /// </summary>
    public string GetRecentOutput()
    {
        lock (_recentOutput)
        {
            return string.Join(Environment.NewLine, _recentOutput);
        }
    }

    /// <summary>
    /// Starts the OpenVPN process with the supplied config, waits for CONNECTED state (up to 30 s).
    /// Throws on failure with a user-friendly message.
    /// </summary>
    public async Task StartAsync(string configContent, string? passphrase, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configContent);

        // Stop any existing tunnel first
        await StopAsync(ct);

        // Reset state for new connection attempt
        _lastError = null;
        _currentPassphrase = passphrase;
        Interlocked.Exchange(ref _bytesIn, 0);
        Interlocked.Exchange(ref _bytesOut, 0);
        _localIp = null;
        lock (_recentOutput) { _recentOutput.Clear(); }

        var openVpnExe = ResolveOpenVpnPath();
        if (openVpnExe is null)
            throw new FileNotFoundException(
                "OpenVPN executable not found. Ensure the installer deployed openvpn.exe correctly.");

        EnsureTapDriverPresent();

        // Write .ovpn config to a secure temp file
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LibreGuardVPN", "temp");
        Directory.CreateDirectory(configDir);

        var configPath = Path.Combine(configDir, $"ovpn_{Guid.NewGuid():N}.ovpn");
        await File.WriteAllTextAsync(configPath, configContent, ct);

        // Write auth-user-pass file if passphrase provided
        string? authFilePath = null;
        if (!string.IsNullOrEmpty(passphrase))
        {
            authFilePath = Path.Combine(configDir, $"auth_{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(authFilePath, $"client\n{passphrase}", ct);
        }

        var arguments = BuildArguments(configPath, authFilePath, ManagementPort);
        _logger.LogInformation("Starting OpenVPN: {Exe} {Args}", openVpnExe, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = openVpnExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.Exited += (_, _) =>
        {
            _logger.LogWarning("OpenVPN process exited with code {ExitCode}", process.ExitCode);
            TryDeleteFile(configPath);
            if (authFilePath is not null)
                TryDeleteFile(authFilePath);

            if (_state is not (OpenVpnState.Disconnected or OpenVpnState.Disconnecting))
            {
                _lastError = $"OpenVPN process exited unexpectedly (exit code {process.ExitCode}).";
                SetState(OpenVpnState.Error);
            }
        };

        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        lock (_lock)
        {
            _process = process;
        }

        process.Start();
        _logger.LogInformation("OpenVPN process started (PID={Pid})", process.Id);
        SetState(OpenVpnState.Connecting);

        // Capture stdout/stderr for logging and output buffer
        _ = CaptureOutputAsync(process, _monitorCts.Token);

        // Connect to the management interface
        var mgmtConnected = await TryConnectManagementAsync(ManagementPort, _monitorCts.Token);
        if (mgmtConnected)
        {
            _ = MonitorManagementInterfaceAsync(_monitorCts.Token);
        }
        else
        {
            _logger.LogWarning("Management interface unavailable � using stdout-only monitoring");
        }

        // Wait briefly for immediate process failure
        await Task.Delay(2000, ct);

        if (process.HasExited)
        {
            // Process exited immediately � build a detailed error message from captured output
            var recentOutput = GetRecentOutput();
            var errorMessage = ClassifyProcessError(process.ExitCode, recentOutput);
            _lastError = errorMessage;
            _logger.LogError("OpenVPN exited immediately (code {ExitCode}): {Error}", process.ExitCode, errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        // Wait for CONNECTED state (works for both mgmt interface and stdout-based monitoring)
        await WaitForStateAsync(OpenVpnState.Connected, TimeSpan.FromSeconds(ConnectionTimeoutSeconds), ct);

        _logger.LogInformation("OpenVPN connected successfully (state={State})", _state);
    }

    /// <summary>
    /// Gracefully stops the OpenVPN process (SIGTERM via management, then kill fallback).
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _currentPassphrase = null;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        if (IsActive)
            SetState(OpenVpnState.Disconnecting);

        // Graceful shutdown via management interface
        if (_managementWriter is not null)
        {
            try
            {
                await _managementWriter.WriteLineAsync("signal SIGTERM");
                await _managementWriter.FlushAsync();

                Process? proc;
                lock (_lock) { proc = _process; }
                if (proc is not null && !proc.HasExited)
                    proc.WaitForExit(GracefulShutdownMs);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // Management interface already closed
            }
        }

        CloseManagementConnection();

        Process? process;
        lock (_lock) { process = _process; }

        if (process is null)
        {
            _localIp = null;
            SetState(OpenVpnState.Disconnected);
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                _logger.LogWarning("OpenVPN did not exit gracefully, killing process");
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(ForceKillMs))
                    throw new TimeoutException("OpenVPN process did not exit after forced termination.");
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited
        }

        // Preserve the process handle when termination throws or times out so a later
        // teardown attempt can retry it. Only publish Disconnected after exit is known.
        lock (_lock)
        {
            if (ReferenceEquals(_process, process))
                _process = null;
        }

        process.Dispose();
        _localIp = null;
        SetState(OpenVpnState.Disconnected);
        _logger.LogInformation("OpenVPN stopped");
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        CloseManagementConnection();

        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            process.Dispose();
        }
    }

    #region Management Interface

    private async Task<bool> TryConnectManagementAsync(int port, CancellationToken ct)
    {
        for (var attempt = 0; attempt < ManagementConnectRetries; attempt++)
        {
            try
            {
                await Task.Delay(ManagementConnectDelayMs, ct);

                var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, ct);

                var stream = client.GetStream();
                _managementReader = new StreamReader(stream, Encoding.UTF8);
                _managementWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
                _managementClient = client;

                _logger.LogInformation("Connected to OpenVPN management interface on port {Port}", port);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                // Not ready yet
            }
        }

        _logger.LogWarning("Failed to connect to management interface; using stdout fallback");
        return false;
    }

    private async Task MonitorManagementInterfaceAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _managementReader is not null)
            {
                var line = await _managementReader.ReadLineAsync(ct);
                if (line is null)
                    break;

                _logger.LogDebug("[mgmt] {Line}", line);
                AppendOutput($"[mgmt] {line}");
                await ParseManagementLineAsync(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during disconnect
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogWarning("Management interface connection lost: {Message}", ex.Message);
        }
    }

    private async Task ParseManagementLineAsync(string line)
    {
        if (line.StartsWith(">STATE:", StringComparison.Ordinal))
        {
            var parts = line[7..].Split(',');
            if (parts.Length >= 2)
            {
                var state = MapOpenVpnState(parts[1]);
                _logger.LogInformation("Management state update: {RawState} -> {MappedState}", parts[1], state);

                if (state == OpenVpnState.Connected && parts.Length >= 4)
                {
                    _localIp = parts[3];
                }
                else if (state == OpenVpnState.Disconnected)
                {
                    _localIp = null;
                }

                SetState(state);
            }
        }
        else if (line.StartsWith(">PASSWORD:", StringComparison.Ordinal))
        {
            if (line.Contains("Verification Failed", StringComparison.OrdinalIgnoreCase))
            {
                _lastError = "VPN authentication failed. Check your credentials.";
                SetState(OpenVpnState.AuthFailed);
            }
            else if (line.Contains("Need", StringComparison.OrdinalIgnoreCase))
            {
                // OpenVPN is asking for a password (e.g., private key passphrase)
                // Format: >PASSWORD:Need 'Private Key' password
                _logger.LogInformation("Management password request: {Line}", line);

                if (!string.IsNullOrEmpty(_currentPassphrase))
                {
                    // Extract the password label (e.g., "Private Key")
                    var labelStart = line.IndexOf('\'') + 1;
                    var labelEnd = line.IndexOf('\'', labelStart);
                    var label = labelStart > 0 && labelEnd > labelStart
                        ? line[labelStart..labelEnd]
                        : "Auth";

                    var sent = await SendManagementCommandAsync($"password '{label}' '{_currentPassphrase}'");
                    _logger.LogInformation("Responded to password prompt '{Label}': sent={Sent}", label, sent);
                }
                else
                {
                    _lastError = $"OpenVPN requires a password but none was provided. Prompt: {line}";
                    _logger.LogWarning("No passphrase available to respond to: {Line}", line);
                    SetState(OpenVpnState.Error);
                }
            }
        }
        else if (line.StartsWith(">FATAL:", StringComparison.Ordinal))
        {
            _lastError = $"OpenVPN fatal error: {line[7..]}";
            _logger.LogError("OpenVPN FATAL: {Detail}", line[7..]);
            SetState(OpenVpnState.Error);
        }
        else if (line.StartsWith(">HOLD:", StringComparison.Ordinal))
        {
            _logger.LogInformation("Releasing management hold");
            var sent = await SendManagementCommandAsync("hold release");
            if (!sent)
            {
                _lastError = "Failed to send 'hold release' to OpenVPN management interface.";
                _logger.LogError("Failed to send hold release � OpenVPN will remain paused");
                SetState(OpenVpnState.Error);
            }
            else
            {
                // Enable real-time state notifications so we receive >STATE: lines
                var stateOn = await SendManagementCommandAsync("state on");
                _logger.LogInformation("Sent 'state on' to management interface: {Result}", stateOn);

                // Enable real-time bytecount notifications (every 1 second)
                var bytecountOn = await SendManagementCommandAsync("bytecount 1");
                _logger.LogInformation("Sent 'bytecount 1' to management interface: {Result}", bytecountOn);
            }
        }
        else if (line.StartsWith(">BYTECOUNT:", StringComparison.Ordinal))
        {
            var parts = line[11..].Split(',');
            if (parts.Length >= 2 && long.TryParse(parts[0], out var bytesIn) && long.TryParse(parts[1], out var bytesOut))
            {
                Interlocked.Exchange(ref _bytesIn, bytesIn);
                Interlocked.Exchange(ref _bytesOut, bytesOut);
            }
        }
        else if (line.StartsWith(">INFO:", StringComparison.Ordinal))
        {
            _logger.LogInformation("Management info: {Detail}", line[6..]);
        }
    }

    private async Task<bool> SendManagementCommandAsync(string command)
    {
        if (_managementWriter is null)
            return false;

        try
        {
            await _managementWriter.WriteLineAsync(command);
            await _managementWriter.FlushAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            return false;
        }
    }

    private void CloseManagementConnection()
    {
        _managementReader?.Dispose();
        _managementReader = null;
        _managementWriter?.Dispose();
        _managementWriter = null;
        _managementClient?.Dispose();
        _managementClient = null;
    }

    #endregion

    #region Stdout/Stderr Capture

    private async Task CaptureOutputAsync(Process process, CancellationToken ct)
    {
        var stdoutTask = CaptureStreamAsync(process.StandardOutput, "stdout", ct);
        var stderrTask = CaptureStreamAsync(process.StandardError, "stderr", ct);
        await Task.WhenAll(stdoutTask, stderrTask);
    }

    private async Task CaptureStreamAsync(StreamReader reader, string source, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;

                _logger.LogDebug("[OpenVPN:{Source}] {Line}", source, line);
                AppendOutput($"[{source}] {line}");
                ParseStdoutLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void AppendOutput(string line)
    {
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > MaxOutputLines)
                _recentOutput.Dequeue();
        }
    }

    private void ParseStdoutLine(string line)
    {
        // Always parse stdout as a safety net � management >STATE: is the primary source,
        // but stdout detection ensures we don't miss state changes if management is slow.
        if (line.Contains("Initialization Sequence Completed", StringComparison.OrdinalIgnoreCase))
            SetState(OpenVpnState.Connected);
        else if (line.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase))
            SetState(OpenVpnState.AuthFailed);
        else if (line.Contains("SIGTERM", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("process exiting", StringComparison.OrdinalIgnoreCase))
            SetState(OpenVpnState.Disconnecting);
        else if (line.Contains("Restart pause", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("RECONNECTING", StringComparison.OrdinalIgnoreCase))
            SetState(OpenVpnState.Reconnecting);
        else if (line.Contains("All TAP-Windows adapters", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("There are no TAP-Windows adapters", StringComparison.OrdinalIgnoreCase))
            SetState(OpenVpnState.TapMissing);
        else if (line.Contains("VERIFY ERROR", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("certificate has expired", StringComparison.OrdinalIgnoreCase))
            SetState(OpenVpnState.CertificateError);
    }

    #endregion

    #region Helpers

    private static string BuildArguments(string configPath, string? authFilePath, int managementPort)
    {
        var sb = new StringBuilder();
        sb.Append($"--config \"{configPath}\"");

        if (authFilePath is not null)
            sb.Append($" --auth-user-pass \"{authFilePath}\"");

        sb.Append($" --management 127.0.0.1 {managementPort}");
        sb.Append(" --management-query-passwords");
        sb.Append(" --management-hold");
        sb.Append(" --verb 3 --connect-retry 3 --connect-retry-max 3");

        return sb.ToString();
    }

    private async Task WaitForStateAsync(OpenVpnState target, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (_state != target && !timeoutCts.Token.IsCancellationRequested)
            {
                // Detect terminal error states (including unexpected process exit)
                if (_state is OpenVpnState.AuthFailed or OpenVpnState.TapMissing
                    or OpenVpnState.CertificateError or OpenVpnState.Error)
                {
                    var detail = _lastError ?? GetStateErrorMessage(_state);
                    var recentOutput = GetRecentOutput();
                    if (!string.IsNullOrWhiteSpace(recentOutput))
                        detail += $"\n--- OpenVPN output ---\n{recentOutput}";

                    throw new InvalidOperationException(detail);
                }

                // Detect unexpected disconnect (process exited without error state)
                Process? proc;
                lock (_lock) { proc = _process; }
                if (proc is null or { HasExited: true })
                {
                    var recentOutput = GetRecentOutput();
                    var detail = _lastError ?? "OpenVPN process exited unexpectedly.";
                    if (!string.IsNullOrWhiteSpace(recentOutput))
                        detail += $"\n--- OpenVPN output ---\n{recentOutput}";

                    throw new InvalidOperationException(detail);
                }

                await Task.Delay(250, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var recentOutput = GetRecentOutput();
            var detail = $"OpenVPN did not reach connected state within {timeout.TotalSeconds}s. Current state: {_state}.";
            if (!string.IsNullOrWhiteSpace(recentOutput))
                detail += $"\n--- OpenVPN output ---\n{recentOutput}";

            _lastError = detail;
            throw new TimeoutException(detail);
        }
    }

    private static string GetStateErrorMessage(OpenVpnState state) => state switch
    {
        OpenVpnState.AuthFailed =>
            "VPN authentication failed. Check your credentials or request a new configuration.",
        OpenVpnState.TapMissing =>
            "TAP/Wintun network adapter not found. Reinstall LibreGuard VPN to repair the driver.",
        OpenVpnState.CertificateError =>
            "VPN certificate is invalid or expired. Fetch a new configuration from the server.",
        OpenVpnState.Error =>
            "OpenVPN encountered a fatal error. Check the service logs for details.",
        _ => $"Unexpected OpenVPN state: {state}"
    };

    private static string ClassifyProcessError(int exitCode, string stderr)
    {
        if (stderr.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("no TAP", StringComparison.OrdinalIgnoreCase))
            return "TAP/Wintun network adapter not found. Reinstall LibreGuard VPN to repair the driver.";

        if (stderr.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase))
            return "VPN authentication failed. Check your credentials or request a new configuration.";

        if (stderr.Contains("VERIFY ERROR", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("certificate has expired", StringComparison.OrdinalIgnoreCase))
            return "VPN certificate is invalid or expired. Fetch a new configuration from the server.";

        return $"OpenVPN process exited with code {exitCode}. {stderr}";
    }

    private static OpenVpnState MapOpenVpnState(string state) => state.Trim().ToUpperInvariant() switch
    {
        "CONNECTING" or "WAIT" or "GET_CONFIG" or "ASSIGN_IP" or "ADD_ROUTES"
            or "RESOLVE" or "TCP_CONNECT" => OpenVpnState.Connecting,
        "AUTH" => OpenVpnState.Authenticating,
        "CONNECTED" => OpenVpnState.Connected,
        "RECONNECTING" => OpenVpnState.Reconnecting,
        "EXITING" => OpenVpnState.Disconnecting,
        _ => OpenVpnState.Unknown
    };

    private void SetState(OpenVpnState state)
    {
        if (_state == state)
            return;

        _logger.LogInformation("OpenVPN state: {Old} -> {New}", _state, state);
        _state = state;
    }

    /// <summary>
    /// Resolves the path to openvpn.exe: bundled path first, then registry, then default install, then PATH.
    /// </summary>
    private static string? ResolveOpenVpnPath()
    {
        // 1. Bundled with installer (preferred)
        if (File.Exists(BundledOpenVpnPath))
            return BundledOpenVpnPath;

        // 2. Registry (OpenVPN Community installer)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(OpenVpnRegistryKey);
            if (key?.GetValue("exe_path") is string registryPath && File.Exists(registryPath))
                return registryPath;

            if (key?.GetValue("") is string installDir)
            {
                var candidate = Path.Combine(installDir, "bin", "openvpn.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { }

        // 3. Default install location
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe");
        if (File.Exists(defaultPath))
            return defaultPath;

        // 4. PATH environment variable
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "openvpn.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void EnsureTapDriverPresent()
    {
        if (IsTapDriverInstalled())
            return;

        throw new InvalidOperationException(
            "TAP/Wintun network adapter not found. Reinstall LibreGuard VPN to repair the network driver.");
    }

    private static bool IsTapDriverInstalled()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetworkAdapterClassKey);
            if (classKey is null)
                return false;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                try
                {
                    using var adapterKey = classKey.OpenSubKey(subKeyName);
                    if (adapterKey?.GetValue("ComponentId") is string componentId)
                    {
                        if (IsKnownTapDriverComponentId(componentId))
                            return true;
                    }
                }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return true; // Cannot verify; let OpenVPN report the error
        }

        return false;
    }

    internal static bool IsKnownTapDriverComponentId(string componentId)
    {
        foreach (var tapId in TapComponentIds)
        {
            if (componentId.Contains(tapId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    #endregion
}

/// <summary>
/// Internal OpenVPN process states tracked by the service.
/// String representation is sent back to the client via pipe responses.
/// </summary>
internal enum OpenVpnState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    Reconnecting,
    Disconnecting,
    AuthFailed,
    TapMissing,
    CertificateError,
    Error,
    Unknown
}

internal sealed record OpenVpnHealthSnapshot(
    bool OpenVpnInstalled,
    string? OpenVpnExePath,
    bool OpenVpnDriverInstalled,
    string? SetupRequiredReason);

