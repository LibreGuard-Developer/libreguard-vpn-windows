using System.Windows;
using LibreGuard.Common.Windows;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LibreGuard_VPN_Desktop;

/// <summary>
/// Application entry point — configures DI container and launches the main window.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private bool _mainWindowReady;
    private readonly object _pendingDeepLinksLock = new();
    private readonly List<string> _pendingDeepLinks = [];
    public IServiceProvider? Services => _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppTrace.Initialize();
        System.Diagnostics.Trace.WriteLine("[AppStartup] Starting LibreGuard VPN Desktop.");

        var singleInstanceService = new SingleInstanceService();
        if (!singleInstanceService.IsFirstInstance())
        {
            System.Diagnostics.Trace.WriteLine("[AppStartup] Existing instance detected; forwarding arguments and exiting.");
            if (e.Args.Length > 0)
            {
                singleInstanceService.SendDeepLinkToRunningInstanceAsync(e.Args[0]).GetAwaiter().GetResult();
            }
            Current.Shutdown();
            return;
        }

        singleInstanceService.RegisterUriScheme();
        singleInstanceService.StartListening();
        singleInstanceService.DeepLinkReceived += OnEarlyDeepLinkReceived;

        var services = new ServiceCollection();
        services.AddSingleton(singleInstanceService);
        ConfigureServices(services);
        System.Diagnostics.Trace.WriteLine("[AppStartup] Building service provider.");
        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.GetRequiredService<WindowsNotificationIdentityService>().Initialize();

        // Keep the Windows startup registry entry in sync with the AutoConnect setting.
        // This corrects the entry if the exe was moved/reinstalled since the setting was last toggled.
        var userSettingsService = _serviceProvider.GetRequiredService<IUserSettingsService>();
        var themeService = _serviceProvider.GetRequiredService<IThemeService>();
        System.Diagnostics.Trace.WriteLine("[AppStartup] Applying theme and startup registration.");
        themeService.ApplyPreferenceAsync(userSettingsService.Settings.ThemePreference).GetAwaiter().GetResult();
        WindowsStartupService.SetLaunchAtStartup(userSettingsService.Settings.AutoConnect);

        var killSwitchManager = _serviceProvider.GetRequiredService<KillSwitchManager>();
        _ = killSwitchManager.InitializeAsync();
        _serviceProvider.GetRequiredService<AuthSessionVpnDisconnectService>();

        System.Diagnostics.Trace.WriteLine("[AppStartup] Creating main window.");
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        _mainWindowReady = true;

        if (e.Args.Length > 0)
        {
            mainViewModel.HandleDeepLink(e.Args[0]);
        }

        foreach (var pendingDeepLink in DrainPendingDeepLinks())
            mainViewModel.HandleDeepLink(pendingDeepLink);

        mainWindow.Show();
        _serviceProvider.GetRequiredService<TrayIconService>().Initialize();
        System.Diagnostics.Trace.WriteLine("[AppStartup] Main window shown.");

        _ = EnsureVpnServiceRunningInBackgroundAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisconnectVpnOnExit();
        _serviceProvider?.GetService<TrayIconService>()?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private void OnEarlyDeepLinkReceived(object? sender, string deepLink)
    {
        if (_mainWindowReady)
            return;

        if (DeepLinkParser.TryParse(deepLink, out var payload) && payload.Action == DeepLinkAction.Shutdown)
        {
            System.Diagnostics.Trace.WriteLine("[AppStartup] Early shutdown deep link received before main window was ready.");
            Current.Dispatcher.BeginInvoke(() => Current.Shutdown());
            return;
        }

        lock (_pendingDeepLinksLock)
        {
            _pendingDeepLinks.Add(deepLink);
        }
    }

    private string[] DrainPendingDeepLinks()
    {
        lock (_pendingDeepLinksLock)
        {
            var pending = _pendingDeepLinks.ToArray();
            _pendingDeepLinks.Clear();
            return pending;
        }
    }

    private async Task EnsureVpnServiceRunningInBackgroundAsync()
    {
        if (_serviceProvider is null)
            return;

        try
        {
            System.Diagnostics.Trace.WriteLine("[AppStartup] Starting VPN service availability check.");
            var vpnServiceLifecycle = _serviceProvider.GetRequiredService<IVpnServiceLifecycleService>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await vpnServiceLifecycle.EnsureServiceRunningAsync(cts.Token);
            System.Diagnostics.Trace.WriteLine("[AppStartup] VPN service availability check completed.");
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine("[AppStartup] VPN service availability check timed out.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[AppStartup] VPN service availability check failed: {ex}");
        }
    }

    private void DisconnectVpnOnExit()
    {
        if (_serviceProvider is null)
            return;

        try
        {
            var shutdownService = _serviceProvider.GetService<VpnShutdownService>();
            shutdownService?.DisconnectOnExitAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Failed to disconnect VPN on application exit: {ex}");
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        AppIdentity.ApplyCurrentProcessAppUserModelId();

        // Core infrastructure
        services.AddSingleton<ILoggerService, FileLoggerService>();
        services.AddSingleton<TokenStorageService>();
        services.AddSingleton<DeviceKeyService>();
        services.AddSingleton<ApiHttpClientService>();
        services.AddSingleton<WindowsNotificationIdentityService>();
        services.AddSingleton<TrayNotificationBridge>();
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<TrayIconService>();

        // Utilities and helpers
        services.AddSingleton<IGoogleAuthService, GoogleAuthService>();
        services.AddSingleton<PingService>();


        // API-backed services
        services.AddSingleton<IAuthenticationService, ApiAuthenticationService>();
        services.AddSingleton<IServerService, ApiServerService>();
        services.AddSingleton<ISubscriptionService, ApiSubscriptionService>();
        services.AddSingleton<IAccountPlanService, AccountPlanService>();
        services.AddSingleton<IDnsSettingsService, ApiDnsSettingsService>();

        // VPN configuration and connection
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<ISystemThemeReader, WindowsSystemThemeReader>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<VpnConfigStorageService>();
        services.AddSingleton<CertificateCacheService>();
        services.AddSingleton<VpnServiceClient>();
        services.AddSingleton<IVpnServiceClient>(sp => sp.GetRequiredService<VpnServiceClient>());
        services.AddSingleton<IVpnServiceLifecycleService, VpnServiceLifecycleService>();
        services.AddSingleton<IOpenVpnDependencyService, OpenVpnDependencyService>();
        services.AddSingleton<VpnShutdownService>();
        services.AddSingleton<IVpnConfigService, ApiVpnConfigService>();
        services.AddSingleton<IVpnConnectionService, WinVpnConnectionService>();
        services.AddSingleton<IKillSwitchService, KillSwitchService>();
        services.AddSingleton<KillSwitchManager>();
        services.AddSingleton<AuthSessionVpnDisconnectService>();

        // Services that remain local
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IStatisticsService, LocalStatisticsService>();
        services.AddSingleton<ICardCheckoutPresenter, CardCheckoutPresenter>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ServerListViewModel>();
        services.AddTransient<StatisticsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<UpgradeViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<RegisterViewModel>();
        services.AddTransient<ForgotPasswordViewModel>();
        services.AddTransient<TwoFactorSetupModalViewModel>();
        services.AddTransient<TwoFactorDisableConfirmationViewModel>();
        services.AddTransient<LogoutConfirmationViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();
    }
}
