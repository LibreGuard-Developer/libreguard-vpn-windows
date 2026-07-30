using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Stores JWT access token, refresh token, and session metadata.
/// Uses a stable Windows machine GUID as the device identifier.
/// </summary>
public sealed class TokenStorageService
{
    private readonly object _lock = new();
    private string? _accessToken;
    private string? _refreshToken;

    private readonly string _sessionFilePath;

    public TokenStorageService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LibreGuardVPN",
            "session.secure"))
    {
    }

    internal TokenStorageService(string sessionFilePath)
    {
        _sessionFilePath = sessionFilePath;
        LoadSession();
    }

    public string? AccessToken
    {
        get { lock (_lock) return _accessToken; }
        set { lock (_lock) _accessToken = value; }
    }

    public string? RefreshToken
    {
        get { lock (_lock) return _refreshToken; }
        set { lock (_lock) _refreshToken = value; }
    }

    public string? UserId { get; set; }
    public string? Email { get; set; }
    public string? PlanType { get; set; }

    /// <summary>
    /// Fired when the session tokens are updated or cleared.
    /// </summary>
    public event Action? SessionChanged;

    /// <summary>
    /// Whether the user has a complete, refreshable session.
    /// </summary>
    public bool HasToken
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrWhiteSpace(_accessToken) &&
                    !string.IsNullOrWhiteSpace(_refreshToken);
            }
        }
    }

    /// <summary>
    /// Returns a stable device identifier derived from the Windows machine GUID.
    /// Falls back to a randomly-generated GUID persisted in user-scoped settings.
    /// </summary>
    public string DeviceId { get; } = ResolveDeviceId();

    /// <summary>
    /// Stores all session tokens from a successful login/refresh.
    /// </summary>
    public void StoreSession(string accessToken, string refreshToken, string userId, string email, string? planType)
    {
        lock (_lock)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
        }

        UserId = userId;
        Email = email;
        PlanType = planType;

        SaveSession();
        SessionChanged?.Invoke();
    }

    /// <summary>
    /// Updates the cached subscription plan without replacing the current session tokens.
    /// </summary>
    public void UpdatePlanType(string? planType)
    {
        if (string.Equals(PlanType, planType, StringComparison.OrdinalIgnoreCase))
            return;

        PlanType = planType;
        SaveSession();
        SessionChanged?.Invoke();
    }

    /// <summary>
    /// Clears all stored tokens and session data.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _accessToken = null;
            _refreshToken = null;
        }

        UserId = null;
        Email = null;
        PlanType = null;

        if (File.Exists(_sessionFilePath))
        {
            try
            {
                File.Delete(_sessionFilePath);
            }
            catch { /* Best effort */ }
        }

        SessionChanged?.Invoke();
    }

    private void SaveSession()
    {
        try
        {
            if (_accessToken == null || _refreshToken == null || UserId == null || Email == null)
                return;

            var data = new SessionData(_accessToken, _refreshToken, UserId, Email, PlanType);
            var json = JsonSerializer.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            // ProtectedData is only supported on Windows
            if (OperatingSystem.IsWindows())
            {
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(_sessionFilePath)!);
                File.WriteAllBytes(_sessionFilePath, protectedBytes);
            }
        }
        catch
        {
            // Fail silently
        }
    }

    private void LoadSession()
    {
        if (!File.Exists(_sessionFilePath))
            return;

        try
        {
            byte[] bytes;
            if (OperatingSystem.IsWindows())
            {
                var protectedBytes = File.ReadAllBytes(_sessionFilePath);
                bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            }
            else
            {
                return; 
            }

            var json = Encoding.UTF8.GetString(bytes);
            var data = JsonSerializer.Deserialize<SessionData>(json);

            if (data is null ||
                string.IsNullOrWhiteSpace(data.AccessToken) ||
                string.IsNullOrWhiteSpace(data.RefreshToken) ||
                string.IsNullOrWhiteSpace(data.UserId) ||
                string.IsNullOrWhiteSpace(data.Email))
            {
                System.Diagnostics.Debug.WriteLine("[TokenStorageService] Discarding incomplete persisted session.");
                Clear();
                return;
            }

            lock (_lock)
            {
                _accessToken = data.AccessToken;
                _refreshToken = data.RefreshToken;
            }
            UserId = data.UserId;
            Email = data.Email;
            PlanType = data.PlanType;
        }
        catch
        {
            Clear();
        }
    }

    private record SessionData(string AccessToken, string RefreshToken, string UserId, string Email, string? PlanType);

    private static string ResolveDeviceId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var machineGuid = key?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrEmpty(machineGuid))
                return $"win-{machineGuid}";
        }
        catch
        {
            // Registry access may be restricted in some environments.
        }

        // Fallback: generate and persist a GUID in user app data.
        var fallbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LibreGuardVPN",
            "device_id");

        try
        {
            if (File.Exists(fallbackPath))
            {
                var stored = File.ReadAllText(fallbackPath).Trim();
                if (!string.IsNullOrEmpty(stored))
                    return stored;
            }

            var newId = $"win-{Guid.NewGuid()}";
            Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
            File.WriteAllText(fallbackPath, newId);
            return newId;
        }
        catch
        {
            return $"win-{Guid.NewGuid()}";
        }
    }
}
