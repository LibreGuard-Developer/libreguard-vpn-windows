namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Simple navigation service that tracks the active view key and raises change events.
/// </summary>
internal sealed class NavigationService : INavigationService
{
    public string CurrentView { get; private set; } = "home";

    public event EventHandler<string>? ViewChanged;

    public void NavigateTo(string viewKey)
    {
        if (CurrentView == viewKey)
            return;

        CurrentView = viewKey;
        ViewChanged?.Invoke(this, viewKey);
    }
}
