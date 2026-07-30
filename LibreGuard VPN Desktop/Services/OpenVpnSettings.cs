using System.IO;
using System.Text.Json;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Configurable settings for the OpenVPN tunnel strategy.
/// Persisted to %LocalAppData%\LibreGuardVPN\openvpn_settings.json.
/// </summary>
internal sealed record OpenVpnSettings
{
    private static readonly string SettingsPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LibreGuardVPN",
            "openvpn_settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// User-specified path to openvpn.exe. Null means auto-detect.
    /// </summary>
    public string? OpenVpnExePath { get; init; }

    /// <summary>
    /// TCP port for the OpenVPN management interface on 127.0.0.1.
    /// </summary>
    public int ManagementPort { get; init; } = 7505;

    /// <summary>
    /// Whether to automatically reconnect on unexpected disconnection.
    /// </summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>
    /// Maximum number of reconnection attempts before giving up.
    /// </summary>
    public int MaxReconnectAttempts { get; init; } = 5;

    /// <summary>
    /// Backoff delays in seconds for each successive reconnection attempt.
    /// The last value is repeated for attempts beyond the array length.
    /// </summary>
    public int[] ReconnectBackoffSeconds { get; init; } = [1, 2, 4, 8, 16, 30];

    /// <summary>
    /// Loads settings from disk. Returns defaults if the file is missing or corrupt.
    /// </summary>
    public static OpenVpnSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new OpenVpnSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<OpenVpnSettings>(json, JsonOptions) ?? new OpenVpnSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenVpnSettings] Failed to load settings: {ex.Message}");
            return new OpenVpnSettings();
        }
    }

    /// <summary>
    /// Persists settings to disk. Logs warning on failure but does not throw.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (directory is not null)
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenVpnSettings] Failed to save settings: {ex.Message}");
        }
    }
}
