using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly MainViewModel _mainViewModel;
    private readonly IAuthenticationService _authenticationService;
    private readonly TrayNotificationBridge _notificationBridge;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private Icon? _trayIcon;
    private bool _initialized;
    private bool _disposed;

    public TrayIconService(
        MainViewModel mainViewModel,
        IAuthenticationService authenticationService,
        TrayNotificationBridge notificationBridge)
    {
        _mainViewModel = mainViewModel;
        _authenticationService = authenticationService;
        _notificationBridge = notificationBridge;

        _menu = new ContextMenuStrip();
        _menu.Opening += OnMenuOpening;

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = TrayTooltipBuilder.Build(CreateTooltipState()),
            Visible = false
        };
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notificationBridge.NotificationRequested += OnTrayNotificationRequested;

        _mainViewModel.Dashboard.PropertyChanged += OnDashboardPropertyChanged;
        _mainViewModel.ServerList.GroupedServers.CollectionChanged += (_, _) =>
        {
            if (_menu.Visible)
                RebuildMenu();
        };
        _authenticationService.SessionChanged += OnSessionChanged;
    }

    public void Initialize()
    {
        if (_initialized)
            return;

        _trayIcon = LoadTrayIcon();
        _notifyIcon.Icon = _trayIcon;
        _notifyIcon.Visible = true;
        _initialized = true;

        UpdateTooltip();
        RebuildMenu();
        _ = PrimeServersAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _authenticationService.SessionChanged -= OnSessionChanged;
        _notificationBridge.NotificationRequested -= OnTrayNotificationRequested;
        _mainViewModel.Dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        _notifyIcon.MouseClick -= OnNotifyIconMouseClick;
        _menu.Opening -= OnMenuOpening;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _trayIcon?.Dispose();
    }

    private void OnTrayNotificationRequested(string title, string body)
    {
        if (!_initialized)
            return;

        try
        {
            _notifyIcon.ShowBalloonTip(5000, title, body, ToolTipIcon.None);
        }
        catch
        {
            // Notification delivery should never interrupt VPN state handling.
        }
    }

    private static Icon LoadTrayIcon()
    {
        var installedIconPath = Path.Combine(AppContext.BaseDirectory, "LibreGuard_logo_cropped_V3.ico");
        if (File.Exists(installedIconPath))
            return new Icon(installedIconPath);

        var assetIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Images", "LibreGuard_logo_cropped_V3.ico");
        if (File.Exists(assetIconPath))
            return new Icon(assetIconPath);

        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var extracted = Icon.ExtractAssociatedIcon(exePath);
            if (extracted is not null)
                return (Icon)extracted.Clone();
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            BringMainWindowToFront();
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        RebuildMenu();

        if (_authenticationService.IsAuthenticated && _mainViewModel.ServerList.GroupedServers.Count == 0)
            _ = PrimeServersAsync();
    }

    private async void OnSessionChanged()
    {
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
        {
            UpdateTooltip();
            RebuildMenu();
            await PrimeServersAsync();
            return;
        }

        if (System.Windows.Application.Current is not null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateTooltip();
                RebuildMenu();
            });
            await PrimeServersAsync();
        }
    }

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || ShouldRefreshForProperty(e.PropertyName))
            UpdateTooltip();
    }

    private static bool ShouldRefreshForProperty(string propertyName)
    {
        return propertyName is nameof(DashboardViewModel.Status)
            or nameof(DashboardViewModel.ConnectedCity)
            or nameof(DashboardViewModel.ConnectedCountry)
            or nameof(DashboardViewModel.VpnIpAddress)
            or nameof(DashboardViewModel.SelectedServer)
            or nameof(DashboardViewModel.SessionDataMb)
            or nameof(DashboardViewModel.Plan)
            or nameof(DashboardViewModel.MonthlyDataUsedMb)
            or nameof(DashboardViewModel.MonthlyDataLimitMb);
    }

    private void UpdateTooltip()
    {
        if (!_initialized)
            return;

        _notifyIcon.Text = TrayTooltipBuilder.Build(CreateTooltipState());
    }

    private TrayTooltipState CreateTooltipState()
    {
        var dashboard = _mainViewModel.Dashboard;

        return new TrayTooltipState(
            dashboard.Status,
            dashboard.ConnectedCountry,
            dashboard.ConnectedCity,
            dashboard.VpnIpAddress ?? dashboard.SelectedServer?.ServerIp,
            dashboard.SessionDataMb,
            dashboard.Plan,
            dashboard.MonthlyDataUsedMb,
            dashboard.MonthlyDataLimitMb);
    }

    private void RebuildMenu()
    {
        var dashboard = _mainViewModel.Dashboard;
        var state = new TrayMenuBuildState(
            _authenticationService.IsAuthenticated,
            dashboard.Status is not (ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Disconnecting),
            dashboard.Status == ConnectionStatus.Connected,
            dashboard.Plan == UserPlan.Pro,
            _mainViewModel.ServerList.GroupedServers.ToArray());

        _menu.Items.Clear();
        foreach (var entry in TrayMenuBuilder.Build(state))
            _menu.Items.Add(CreateMenuItem(entry));
    }

    private ToolStripItem CreateMenuItem(TrayMenuEntry entry)
    {
        if (entry.IsSeparator)
            return new ToolStripSeparator();

        var item = new ToolStripMenuItem(entry.Text)
        {
            Enabled = entry.Enabled
        };

        if (entry.Action is not null)
            item.Tag = entry.Action;

        if (entry.Children is { Count: > 0 })
        {
            foreach (var child in entry.Children)
                item.DropDownItems.Add(CreateMenuItem(child));
        }
        else if (entry.Action is not null)
        {
            item.Click += OnMenuItemClick;
        }

        return item;
    }

    private async void OnMenuItemClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: TrayMenuAction action })
            return;

        switch (action.Kind)
        {
            case TrayMenuActionKind.QuickConnect:
                await _mainViewModel.Dashboard.QuickConnectFromTrayAsync();
                break;
            case TrayMenuActionKind.Disconnect:
                await _mainViewModel.Dashboard.DisconnectFromTrayAsync();
                break;
            case TrayMenuActionKind.ConnectServer:
                var server = FindServer(action.ServerId);
                if (server is not null)
                    await _mainViewModel.Dashboard.ConnectToServerFromTrayAsync(server);
                break;
            case TrayMenuActionKind.Exit:
                System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Application.Current.MainWindow?.Close());
                break;
        }
    }

    private ServerLocation? FindServer(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            return null;

        return _mainViewModel.ServerList.GroupedServers
            .SelectMany(group => group.Servers)
            .FirstOrDefault(server => string.Equals(server.Id, serverId, StringComparison.Ordinal));
    }

    private async Task PrimeServersAsync()
    {
        if (!_authenticationService.IsAuthenticated)
            return;

        try
        {
            await _mainViewModel.ServerList.LoadServersAsync();
            RebuildMenu();
        }
        catch
        {
            // The tray should remain usable even if the server list cannot be refreshed.
        }
    }

    private static void BringMainWindowToFront()
    {
        if (System.Windows.Application.Current?.MainWindow is not { } window)
            return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        if (!window.IsVisible)
            window.Show();

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}
