using CommunityToolkit.Mvvm.ComponentModel;

namespace LibreGuard_VPN_Desktop.Models;

/// <summary>
/// Represents a VPN server location with connection metadata.
/// </summary>
public sealed class ServerLocation : ObservableObject
{
    private int _pingMs;
    private bool _isFavorite;

    public string Id { get; }
    public string Country { get; }
    public string City { get; }
    public string ServerName { get; }
    public string Flag { get; }
    public string? FlagUrl { get; }
    
    public int PingMs 
    { 
        get => _pingMs; 
        set => SetProperty(ref _pingMs, value); 
    }

    /// <summary>Gets or sets whether this server is in the user's favourites.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

    public int LoadPercent { get; }
    public bool IsPremium { get; }
    public string? ServerIp { get; }
    public string? ServerHostname { get; }
    public int LinkSpeedMbps { get; }
    public int LatencyPingPort { get; }

    public ServerLocation(
        string id,
        string country,
        string city,
        string serverName,
        string flag,
        string? flagUrl,
        int pingMs,
        int loadPercent,
        bool isPremium = false,
        string? serverIp = null,
        string? serverHostname = null,
        int linkSpeedMbps = 100,
        int latencyPingPort = 5001)
    {
        Id = id;
        Country = country;
        City = city;
        ServerName = serverName;
        Flag = flag;
        FlagUrl = flagUrl;
        PingMs = pingMs;
        LoadPercent = loadPercent;
        IsPremium = isPremium;
        ServerIp = serverIp;
        ServerHostname = serverHostname;
        LinkSpeedMbps = linkSpeedMbps;
        LatencyPingPort = latencyPingPort;
    }

    public override bool Equals(object? obj)
    {
        return obj is ServerLocation location && Id == location.Id;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public override string ToString()
    {
        return $"{Flag} {Country} - {City} ({PingMs}ms)";
    }
}
