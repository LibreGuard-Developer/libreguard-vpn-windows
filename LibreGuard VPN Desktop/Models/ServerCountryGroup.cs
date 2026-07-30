using System.Collections.ObjectModel;

namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Represents a country section in the server list.
/// </summary>
public sealed class ServerCountryGroup
{
    public string Country { get; }
    public string Flag { get; }
    public string? FlagUrl { get; }
    public ObservableCollection<ServerLocation> Servers { get; }

    public int ServerCount => Servers.Count;
    public string ServerSummary => ServerCount == 1 ? "1 server" : $"{ServerCount} servers";

    public ServerCountryGroup(string country, string flag, string? flagUrl, IEnumerable<ServerLocation> servers)
    {
        Country = country;
        Flag = flag;
        FlagUrl = flagUrl;
        Servers = new ObservableCollection<ServerLocation>(servers);
    }
}
