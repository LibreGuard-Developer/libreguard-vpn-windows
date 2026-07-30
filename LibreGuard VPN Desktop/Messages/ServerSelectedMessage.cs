using CommunityToolkit.Mvvm.Messaging.Messages;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Messages;

/// <summary>
/// Message sent when a server is selected from the server list.
/// </summary>
public class ServerSelectedMessage : ValueChangedMessage<ServerLocation>
{
    public VpnProtocol Protocol { get; }

    public ServerSelectedMessage(ServerLocation value, VpnProtocol protocol) : base(value)
    {
        Protocol = protocol;
    }
}
