namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Manages view navigation within the application shell.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Navigates to the specified view by key.
    /// </summary>
    void NavigateTo(string viewKey);

    /// <summary>
    /// Gets the currently active view key.
    /// </summary>
    string CurrentView { get; }

    /// <summary>
    /// Raised when the active view changes.
    /// </summary>
    event EventHandler<string>? ViewChanged;
}
