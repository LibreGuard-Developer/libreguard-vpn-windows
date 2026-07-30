using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Service for managing and persisting user-specific application settings.
/// </summary>
public interface IUserSettingsService
{
    /// <summary>
    /// Gets the current user settings.
    /// </summary>
    UserSettings Settings { get; }

    /// <summary>
    /// Raised when settings are saved and may have changed.
    /// </summary>
    event EventHandler? SettingsChanged;

    /// <summary>
    /// Saves the current settings to persistent storage.
    /// </summary>
    Task SaveSettingsAsync();

    /// <summary>
    /// Loads settings from persistent storage.
    /// </summary>
    Task LoadSettingsAsync();
}
