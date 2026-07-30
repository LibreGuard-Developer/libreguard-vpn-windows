using CommunityToolkit.Mvvm.Messaging.Messages;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Messages;

/// <summary>
/// Message broadcast when user settings are updated.
/// </summary>
public sealed class SettingsChangedMessage : ValueChangedMessage<UserSettings>
{
    public SettingsChangedMessage(UserSettings value) : base(value)
    {
    }
}
