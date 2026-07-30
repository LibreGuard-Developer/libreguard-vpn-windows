namespace LibreGuard_VPN_Desktop.Services;

internal sealed class TrayNotificationBridge
{
    public event Action<string, string>? NotificationRequested;

    public void RequestNotification(string title, string body)
    {
        NotificationRequested?.Invoke(title, body);
    }
}
