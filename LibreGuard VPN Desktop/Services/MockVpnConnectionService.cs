using System.Timers;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Simulates VPN connection behavior for UI development and testing.
/// </summary>
internal sealed class MockVpnConnectionService : IVpnConnectionService, IDisposable
{
    private readonly IStatisticsService _statisticsService;
    private readonly System.Timers.Timer _statsTimer;
    private readonly Random _random = new();
    private DateTime _connectedSince;
    private double _sessionDataMb;
    private ServerLocation? _currentServer;

    public MockVpnConnectionService(IStatisticsService statisticsService)
    {
        _statisticsService = statisticsService;
        _statsTimer = new System.Timers.Timer(1000);
        _statsTimer.Elapsed += OnStatsTimerElapsed;
    }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
    public ConnectionStats? CurrentStats { get; private set; }
    public string? VpnIpAddress { get; private set; }
    public string? LastErrorMessage { get; private set; }

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<ConnectionStats>? StatsUpdated;
#pragma warning disable CS0067
    public event EventHandler<string>? ErrorOccurred;
#pragma warning restore CS0067

    public async Task ConnectAsync(ServerLocation server, VpnProtocol protocol, CancellationToken cancellationToken = default)
    {
        SetStatus(ConnectionStatus.Connecting);
        _currentServer = server;

        await Task.Delay(2000, cancellationToken);

        _connectedSince = DateTime.UtcNow;
        _sessionDataMb = 0;
        VpnIpAddress = "198.51.100.78";

        SetStatus(ConnectionStatus.Connected);
        _statsTimer.Start();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _statsTimer.Stop();

        if (Status == ConnectionStatus.Connected && _currentServer != null)
        {
            var end = DateTime.UtcNow;
            var record = new VpnSessionRecord(
                StartTime: _connectedSince,
                EndTime: end,
                ServerName: _currentServer.ServerName,
                DownloadMb: _sessionDataMb * 0.7,
                UploadMb: _sessionDataMb * 0.3);
            
            await _statisticsService.RecordSessionAsync(record);
        }

        if (Status != ConnectionStatus.Disconnected)
        {
            SetStatus(ConnectionStatus.Disconnecting);
            await Task.Delay(420, cancellationToken);
        }

        VpnIpAddress = null;
        CurrentStats = null;
        _currentServer = null;
        SetStatus(ConnectionStatus.Disconnected);
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void OnStatsTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        _sessionDataMb += _random.NextDouble() * 0.8;
        var duration = DateTime.UtcNow - _connectedSince;
        var stats = new ConnectionStats(
            DownloadSpeedMbps: 8 + _random.NextDouble() * 8,
            UploadSpeedMbps: 2 + _random.NextDouble() * 4,
            SessionDataMb: _sessionDataMb,
            Duration: duration,
            SessionDownloadMb: _sessionDataMb * 0.7,
            SessionUploadMb: _sessionDataMb * 0.3);

        CurrentStats = stats;
        StatsUpdated?.Invoke(this, stats);
    }

    public void Dispose()
    {
        _statsTimer.Stop();
        _statsTimer.Dispose();
    }
}
