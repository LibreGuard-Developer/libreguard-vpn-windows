using System.IO;
using LibreGuard.Common.Windows;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Delivers Windows toast notifications for VPN lifecycle events using the WinRT
/// <see cref="ToastNotificationManager"/> API.
/// Notifications are silently suppressed when the user has disabled them in settings.
/// </summary>
internal sealed class WindowsNotificationService : INotificationService
{
    private readonly IUserSettingsService _userSettingsService;
    private readonly WindowsNotificationIdentityService _identityService;
    private readonly TrayNotificationBridge _trayNotificationBridge;

    public WindowsNotificationService(
        IUserSettingsService userSettingsService,
        WindowsNotificationIdentityService identityService,
        TrayNotificationBridge trayNotificationBridge)
    {
        _userSettingsService = userSettingsService;
        _identityService = identityService;
        _trayNotificationBridge = trayNotificationBridge;
    }

    public void NotifyVpnConnecting() =>
        Show(
            "VPN Connecting",
            "LibreGuard VPN is establishing a secure connection.",
            NotificationScenario.Default);

    public void NotifyVpnConnected(string serverName, string city, string country, string? ipAddress)
    {
        var ipInfo = !string.IsNullOrWhiteSpace(ipAddress) ? $" • IP: {ipAddress}" : string.Empty;
        Show(
            "VPN Connected",
            $"Connected to {serverName} in {city}, {country}{ipInfo}.",
            NotificationScenario.Default);
    }

    public void NotifyVpnDisconnected() =>
        Show(
            "VPN Disconnected",
            "LibreGuard VPN is disconnected.",
            NotificationScenario.Default);

    public void NotifyConnectionLost() =>
        Show(
            "VPN Connection Lost",
            "Your VPN connection was unexpectedly dropped.",
            NotificationScenario.Reminder);

    public void NotifyConnectionError(string message) =>
        Show(
            "VPN Connection Failed",
            message,
            NotificationScenario.Reminder);

    public void NotifyKillSwitchEnabled() =>
        Show(
            "Kill Switch Active",
            "Internet access is blocked to prevent IP and DNS leaks.",
            NotificationScenario.Default);

    public void NotifyKillSwitchDisabled() =>
        Show(
            "Kill Switch Disabled",
            "Normal internet access has been restored.",
            NotificationScenario.Default);

    public void NotifyDataUsageWarning(double percentUsed) =>
        Show(
            "Data Usage Warning",
            $"{percentUsed:F0}\u00a0% of your monthly VPN data has been used.",
            NotificationScenario.Reminder);

    public void NotifyDataLimitReached() =>
        Show(
            "Data Limit Reached",
            "You have used all of your monthly VPN data allocation.",
            NotificationScenario.Reminder);

    // -------------------------------------------------------------------------

    private void Show(string title, string body, NotificationScenario scenario)
    {
        if (!_userSettingsService.Settings.Notifications)
            return;

        try
        {
            _identityService.Initialize();

            var xml = BuildToastXml(title, body, scenario, GetNotificationLogoUri());
            var notifier = ToastNotificationManager.CreateToastNotifier(AppIdentity.AppUserModelId);

            // Only send if the notifier is enabled by the OS / user.
            if (notifier.Setting == NotificationSetting.Enabled)
            {
                var toast = new ToastNotification(xml);
                notifier.Show(toast);
            }
            else
            {
                _trayNotificationBridge.RequestNotification(title, body);
            }
        }
        catch (Exception ex)
        {
            // Never let a notification failure surface to the UI.
            System.Diagnostics.Debug.WriteLine($"[Notifications] Failed to show toast: {ex.Message}");
            _trayNotificationBridge.RequestNotification(title, body);
        }
    }

    private static XmlDocument BuildToastXml(string title, string body, NotificationScenario scenario, string? logoUri)
    {
        var scenarioAttr = scenario == NotificationScenario.Reminder ? " scenario=\"reminder\"" : string.Empty;
        var logoImage = string.IsNullOrWhiteSpace(logoUri)
            ? string.Empty
            : $"""<image placement="appLogoOverride" src="{EscapeXml(logoUri)}"/>""";

        var xmlString =
            $"""
            <toast{scenarioAttr}>
              <visual>
                <binding template="ToastGeneric">
                  {logoImage}
                  <text>{EscapeXml(title)}</text>
                  <text>{EscapeXml(body)}</text>
                </binding>
              </visual>
            </toast>
            """;

        var doc = new XmlDocument();
        doc.LoadXml(xmlString);
        return doc;
    }

    private static string? GetNotificationLogoUri()
    {
        var installedIconPath = Path.Combine(AppContext.BaseDirectory, "LibreGuard_logo_cropped_V3.png");
        if (!File.Exists(installedIconPath))
            installedIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Images", "LibreGuard_logo_cropped_V3.png");

        if (File.Exists(installedIconPath))
            return new Uri(installedIconPath).AbsoluteUri;

        var installedIcoPath = Path.Combine(AppContext.BaseDirectory, "LibreGuard_logo_cropped_V3.ico");
        if (!File.Exists(installedIcoPath))
            installedIcoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Images", "LibreGuard_logo_cropped_V3.ico");

        return File.Exists(installedIcoPath)
            ? new Uri(installedIcoPath).AbsoluteUri
            : null;
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    private enum NotificationScenario { Default, Reminder }
}
