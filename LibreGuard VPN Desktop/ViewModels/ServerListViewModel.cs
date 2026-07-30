using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LibreGuard_VPN_Desktop.Messages;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;
using System.Windows.Threading;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Server list with search, sort, favorites, recent, and protocol selector.
/// </summary>
public sealed partial class ServerListViewModel : ObservableObject
{
    private readonly IServerService _serverService;
    private readonly INavigationService _navigationService;
    private readonly IVpnConnectionService _vpnConnectionService;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IAccountPlanService _accountPlanService;
    private readonly Dispatcher _dispatcher;
    private List<ServerLocation> _allServers = [];

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedServerId = "1";

    [ObservableProperty]
    private VpnProtocol _selectedProtocol = VpnProtocol.IKEv2;

    [ObservableProperty]
    private bool _isOpenVpnAvailable = true;

    [ObservableProperty]
    private string _sortBy = string.Empty;

    [ObservableProperty]
    private bool _isRefreshing;

    public ObservableCollection<ServerLocation> FilteredServers { get; } = [];
    public ObservableCollection<ServerCountryGroup> GroupedServers { get; } = [];
    public ObservableCollection<ServerLocation> FavoriteServers { get; } = [];
    public ObservableCollection<ServerLocation> RecentServers { get; } = [];
    public ObservableCollection<string> FavoriteIds { get; } = [];

    public int TotalServerCount => _allServers.Count;
    public int CountryCount => _allServers.Select(s => s.Country).Distinct().Count();

    public ServerListViewModel(IServerService serverService, 
                               INavigationService navigationService, 
                               IVpnConnectionService vpnConnectionService,
                               IUserSettingsService userSettingsService,
                               IAccountPlanService accountPlanService)
    {
        _serverService = serverService;
        _navigationService = navigationService;
        _vpnConnectionService = vpnConnectionService;
        _userSettingsService = userSettingsService;
        _accountPlanService = accountPlanService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _selectedProtocol = _userSettingsService.Settings.DefaultProtocol;
        ApplyPlanState();

        WeakReferenceMessenger.Default.Register<SettingsChangedMessage>(this, (r, m) =>
        {
            SelectedProtocol = m.Value.DefaultProtocol;
        });

        _accountPlanService.PlanChanged += OnPlanChanged;
    }

    public async Task RefreshPlanAsync(bool force = false)
    {
        await _accountPlanService.RefreshAsync(force);
        ApplyPlanState();
    }

    private void OnPlanChanged()
    {
        if (_dispatcher.CheckAccess())
            ApplyPlanState();
        else
            _dispatcher.Invoke(ApplyPlanState);
    }

    private void ApplyPlanState()
    {
        IsOpenVpnAvailable = _accountPlanService.IsOpenVpnAvailable;

        if (!IsOpenVpnAvailable && SelectedProtocol == VpnProtocol.OpenVPN)
            SelectedProtocol = VpnProtocol.IKEv2;
    }

    /// <summary>
    /// Loads servers from the service. Called when view is activated.
    /// </summary>
    [RelayCommand]
    public async Task LoadServersAsync()
    {
        if (_vpnConnectionService.Status != ConnectionStatus.Disconnected && _allServers.Count > 0)
        {
            // Skip fetching from API if connected and we already have servers
            RefreshFavoritesAndRecent();
            return;
        }

        _allServers = [.. await _serverService.GetServersAsync()];
        OnPropertyChanged(nameof(TotalServerCount));
        OnPropertyChanged(nameof(CountryCount));
        ApplyFilter();
        RefreshFavoritesAndRecent();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    partial void OnSortByChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SelectServer(ServerLocation server)
    {
        if (server.IsPremium && !IsOpenVpnAvailable)
            return;

        SelectedServerId = server.Id;
        _serverService.AddRecent(server.Id);
        RefreshFavoritesAndRecent();

        WeakReferenceMessenger.Default.Send(new ServerSelectedMessage(server, SelectedProtocol));
        _navigationService.NavigateTo("home");
    }

    [RelayCommand]
    private void ToggleFavorite(ServerLocation server)
    {
        _serverService.ToggleFavorite(server.Id);
        RefreshFavoritesAndRecent();
    }

    [RelayCommand]
    public async Task RefreshServersAsync()
    {
        IsRefreshing = true;
        await LoadServersAsync();
        IsRefreshing = false;
    }

    private void ApplyFilter()
    {
        var query = SearchQuery.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allServers
            : _allServers.Where(s =>
                s.City.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Country.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.ServerName.Contains(query, StringComparison.OrdinalIgnoreCase));

        var sorted = string.IsNullOrEmpty(SortBy) ? filtered : SortBy switch
        {
            "load" => filtered.OrderBy(s => s.LoadPercent),
            "name" => filtered.OrderBy(s => s.Country),
            _ => filtered.OrderBy(s => s.PingMs),
        };

        var visibleServers = sorted.ToList();

        FilteredServers.Clear();
        foreach (var server in visibleServers)
            FilteredServers.Add(server);

        GroupedServers.Clear();
        foreach (var group in visibleServers
                     .GroupBy(GetCountryKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var servers = group.ToList();
            var first = servers[0];
            var country = GetCountryKey(first);
            GroupedServers.Add(new ServerCountryGroup(country, first.Flag, first.FlagUrl, servers));
        }
    }

    partial void OnSelectedProtocolChanged(VpnProtocol value)
    {
        if (value == VpnProtocol.OpenVPN && !IsOpenVpnAvailable)
        {
            _navigationService.NavigateTo("upgrade");
            SelectedProtocol = VpnProtocol.IKEv2;
        }
    }

    private void RefreshFavoritesAndRecent()
    {
        var favIds = _serverService.GetFavorites();
        FavoriteIds.Clear();
        FavoriteServers.Clear();
        foreach (var id in favIds)
        {
            FavoriteIds.Add(id);
            var s = _allServers.FirstOrDefault(x => x.Id == id);
            if (s is not null) FavoriteServers.Add(s);
        }

        // Sync IsFavorite flag on every server so the star binding updates live
        var favSet = new HashSet<string>(favIds);
        foreach (var server in _allServers)
            server.IsFavorite = favSet.Contains(server.Id);

        var recentIds = _serverService.GetRecent();
        RecentServers.Clear();
        foreach (var id in recentIds)
        {
            var s = _allServers.FirstOrDefault(x => x.Id == id);
            if (s is not null) RecentServers.Add(s);
        }
    }

    private static string GetCountryKey(ServerLocation server)
    {
        return string.IsNullOrWhiteSpace(server.Country)
            ? "Unknown"
            : server.Country.Trim();
    }
}
