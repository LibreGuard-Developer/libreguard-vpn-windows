using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LibreGuard_VPN_Desktop.Messages;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;
using System.Diagnostics;
using System.Windows.Threading;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Settings screen: toggles for security, connection, notifications, and app info.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;
    private readonly IAccountPlanService _accountPlanService;
    private readonly IDnsSettingsService _dnsSettingsService;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IThemeService _themeService;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _adBlockingOperationLock = new(1, 1);

    [ObservableProperty]
    private bool _autoConnect;

    async partial void OnAutoConnectChanged(bool value)
    {
        _userSettingsService.Settings.AutoConnect = value;
        await _userSettingsService.SaveSettingsAsync();
        WindowsStartupService.SetLaunchAtStartup(value);
    }

    [ObservableProperty]
    private bool _killSwitch;

    async partial void OnKillSwitchChanged(bool value)
    {
        if (value && !IsProPlan)
        {
            KillSwitch = false;
            return;
        }
        _userSettingsService.Settings.KillSwitch = value;
        await _userSettingsService.SaveSettingsAsync();
    }

    private bool _twoFactorAuth;
    public bool TwoFactorAuth
    {
        get => _twoFactorAuth;
        set
        {
            if (SetProperty(ref _twoFactorAuth, value))
            {
                OnTwoFactorAuthChanged(value);
            }
        }
    }

    [ObservableProperty]
    private bool _notifications;

    async partial void OnNotificationsChanged(bool value)
    {
        _userSettingsService.Settings.Notifications = value;
        await _userSettingsService.SaveSettingsAsync();
    }

    [ObservableProperty]
    private VpnProtocol _defaultProtocol;

    async partial void OnDefaultProtocolChanged(VpnProtocol value)
    {
        _userSettingsService.Settings.DefaultProtocol = value;
        await _userSettingsService.SaveSettingsAsync();
        WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(_userSettingsService.Settings));
    }

    [ObservableProperty]
    private AppThemePreference _themePreference;

    async partial void OnThemePreferenceChanged(AppThemePreference value)
    {
        _userSettingsService.Settings.ThemePreference = value;
        await _themeService.ApplyPreferenceAsync(value);
        await _userSettingsService.SaveSettingsAsync();
    }

    [ObservableProperty]
    private bool _isOpenVpnAvailable;

    [ObservableProperty]
    private bool _isProPlan;

    partial void OnIsProPlanChanged(bool value)
    {
        if (HasAdBlockingPreference)
            _planDowngradeObservedAfterDnsLoad = !value;

        NotifyAdBlockingDerivedStateChanged();

        if (HasAdBlockingPreference && !IsAdBlockingBusy)
        {
            AdBlockingStatus = BuildAdBlockingStatus(includePropagation: false);
        }
    }

    private bool _hasAdBlockingPreference;
    public bool HasAdBlockingPreference
    {
        get => _hasAdBlockingPreference;
        private set
        {
            if (SetProperty(ref _hasAdBlockingPreference, value))
                NotifyAdBlockingDerivedStateChanged();
        }
    }

    private bool _adBlockingRequestedEnabled;
    public bool AdBlockingRequestedEnabled
    {
        get => _adBlockingRequestedEnabled;
        private set
        {
            if (SetProperty(ref _adBlockingRequestedEnabled, value))
                NotifyAdBlockingDerivedStateChanged();
        }
    }

    private bool _canUseAdBlocking;
    public bool CanUseAdBlocking
    {
        get => _canUseAdBlocking;
        private set
        {
            if (SetProperty(ref _canUseAdBlocking, value))
                NotifyAdBlockingDerivedStateChanged();
        }
    }

    private bool _adBlockingEffectiveEnabled;
    public bool AdBlockingEffectiveEnabled
    {
        get => _adBlockingEffectiveEnabled;
        private set => SetProperty(ref _adBlockingEffectiveEnabled, value);
    }

    private string _adBlockingEffectiveMode = string.Empty;
    public string AdBlockingEffectiveMode
    {
        get => _adBlockingEffectiveMode;
        private set => SetProperty(ref _adBlockingEffectiveMode, value);
    }

    private int _adBlockingPropagationSeconds;
    public int AdBlockingPropagationSeconds
    {
        get => _adBlockingPropagationSeconds;
        private set => SetProperty(ref _adBlockingPropagationSeconds, value);
    }

    private bool _isAdBlockingBusy;
    public bool IsAdBlockingBusy
    {
        get => _isAdBlockingBusy;
        private set
        {
            if (SetProperty(ref _isAdBlockingBusy, value))
                NotifyAdBlockingDerivedStateChanged();
        }
    }

    private string _adBlockingStatus = string.Empty;
    public string AdBlockingStatus
    {
        get => _adBlockingStatus;
        private set => SetProperty(ref _adBlockingStatus, value);
    }

    private bool _adBlockingRequiresUpgrade;
    private bool _planDowngradeObservedAfterDnsLoad;
    private string? _adBlockingPreferenceOwnerKey;
    private string? _observedSessionKey;
    private long _sessionGeneration;

    public bool IsAdBlockingUpgradeRequired =>
        IsLoggedIn &&
        (HasAdBlockingPreference
            ? _adBlockingRequiresUpgrade || _planDowngradeObservedAfterDnsLoad || !CanUseAdBlocking
            : !IsProPlan);

    public bool CanToggleAdBlocking =>
        IsLoggedIn &&
        HasAdBlockingPreference &&
        !IsAdBlockingUpgradeRequired &&
        !IsAdBlockingBusy;

    public string AdBlockingActionText => AdBlockingRequestedEnabled ? "Turn off" : "Turn on";

    private string _currentPlan = "Loading...";
    public string CurrentPlan
    {
        get => _currentPlan;
        set => SetProperty(ref _currentPlan, value);
    }

    [ObservableProperty]
    private string _appVersion = "1.1.0";

    [RelayCommand]
    private void SelectIkeProtocol() => DefaultProtocol = VpnProtocol.IKEv2;

    [RelayCommand]
    private void SelectOpenVpnProtocol()
    {
        if (IsOpenVpnAvailable)
        {
            DefaultProtocol = VpnProtocol.OpenVPN;
        }
        else
        {
            UpgradeCommand.Execute(null);
        }
    }

    private string _userEmail = string.Empty;
    public string UserEmail
    {
        get => _userEmail;
        set => SetProperty(ref _userEmail, value);
    }

    private bool _isLoggedIn;
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set
        {
            if (SetProperty(ref _isLoggedIn, value))
                NotifyAdBlockingDerivedStateChanged();
        }
    }

    public event EventHandler? ShowTwoFactorSetupDialog;
    public event EventHandler? DisableTwoFactorDialog;

    public IAsyncRelayCommand DisableTwoFactorCommand { get; }
    public IAsyncRelayCommand ToggleAdBlockingCommand { get; }

    public IRelayCommand OpenPrivacyPolicyCommand { get; }
    public IRelayCommand OpenTermsOfServiceCommand { get; }
    public IRelayCommand OpenHelpAndSupportCommand { get; }
    public IRelayCommand OpenSourceLicensesCommand { get; }
    public IRelayCommand OpenManageAccountCommand { get; }
    public IRelayCommand UpgradeCommand { get; }

    public SettingsViewModel(IAuthenticationService authService, 
                            IAccountPlanService accountPlanService,
                            IDnsSettingsService dnsSettingsService,
                            INavigationService navigationService,
                            IUserSettingsService userSettingsService,
                            IThemeService themeService)
    {
        _authService = authService;
        _accountPlanService = accountPlanService;
        _dnsSettingsService = dnsSettingsService;
        _userSettingsService = userSettingsService;
        _themeService = themeService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Load initial state from service
        var settings = _userSettingsService.Settings;
        _autoConnect = settings.AutoConnect;
        _killSwitch = settings.KillSwitch;
        _notifications = settings.Notifications;
        _defaultProtocol = settings.DefaultProtocol;
        _themePreference = settings.ThemePreference;

        DisableTwoFactorCommand = new AsyncRelayCommand(DisableTwoFactorAsync);
        ToggleAdBlockingCommand = new AsyncRelayCommand(ToggleAdBlockingAsync, CanExecuteAdBlockingToggle);
        OpenPrivacyPolicyCommand = new RelayCommand(OpenPrivacyPolicy);
        OpenTermsOfServiceCommand = new RelayCommand(OpenTermsOfService);
        OpenHelpAndSupportCommand = new RelayCommand(OpenHelpAndSupport);
        OpenSourceLicensesCommand = new RelayCommand(OpenSourceLicenses);
        OpenManageAccountCommand = new RelayCommand(OpenManageAccount);
        UpgradeCommand = new RelayCommand(() => navigationService.NavigateTo("upgrade"));

        _accountPlanService.PlanChanged += OnPlanChanged;
        _authService.SessionChanged += OnSessionChanged;
        InitializeUserData();
    }

    private void InitializeUserData()
    {
        if (_authService.IsAuthenticated)
        {
            _observedSessionKey = GetCurrentSessionKey();
            IsLoggedIn = true;
            UserEmail = _authService.UserEmail ?? "Not logged in";
            ApplyPlanState();
            _ = LoadUserDataAsync(retryPlanRefresh: false, CancellationToken.None);
            return;
        }

        ApplyLoggedOutState();
    }

    private void ApplyLoggedOutState()
    {
        _observedSessionKey = null;
        IsLoggedIn = false;
        UserEmail = "Not logged in";
        CurrentPlan = "Free";
        IsOpenVpnAvailable = false;
        IsProPlan = false;
        ClearAdBlockingState();

        if (DefaultProtocol == VpnProtocol.OpenVPN)
        {
            DefaultProtocol = VpnProtocol.IKEv2;
        }

        if (AutoConnect)
        {
            AutoConnect = false;
        }
    }

    /// <summary>
    /// Refreshes user data from authentication and subscription services.
    /// Call this when navigating to the settings view.
    /// </summary>
    public async Task RefreshUserDataAsync(
        bool retryPlanRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await LoadUserDataAsync(retryPlanRefresh, cancellationToken);
    }

    private async Task LoadUserDataAsync(bool retryPlanRefresh, CancellationToken cancellationToken)
    {
        if (!_authService.IsAuthenticated)
        {
            ApplyLoggedOutState();
            return;
        }

        var sessionKey = GetCurrentSessionKey();
        var sessionGeneration = _sessionGeneration;
        _observedSessionKey = sessionKey;
        if (HasAdBlockingPreference &&
            !string.Equals(_adBlockingPreferenceOwnerKey, sessionKey, StringComparison.Ordinal))
        {
            ClearAdBlockingState();
        }

        IsLoggedIn = true;
        UserEmail = _authService.UserEmail ?? "Not logged in";
        ApplyPlanState();

        try
        {
            await _accountPlanService.RefreshAsync(retryPlanRefresh, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Keep the cached plan and still refresh the other account-backed settings.
        }

        if (!IsCurrentSession(sessionKey, sessionGeneration))
        {
            if (!_authService.IsAuthenticated)
                ApplyLoggedOutState();
            return;
        }

        ApplyPlanState();
        await LoadAdBlockingPreferenceAsync(sessionKey, sessionGeneration, cancellationToken);

        if (!IsCurrentSession(sessionKey, sessionGeneration))
            return;

        try
        {
            var twoFactorStatus = await _authService.GetTwoFactorStatusAsync(cancellationToken);
            if (twoFactorStatus is null || !IsCurrentSession(sessionKey, sessionGeneration))
                return;

            // Update the backing field to avoid opening a 2FA dialog during refresh.
            _twoFactorAuth = twoFactorStatus.IsEnabled;
            OnPropertyChanged(nameof(TwoFactorAuth));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation leaves the last authoritative state visible.
        }
        catch
        {
            // A 2FA status failure must not discard successfully loaded DNS settings.
        }
    }

    private void OnSessionChanged()
    {
        void ApplySessionState()
        {
            var currentSessionKey = _authService.IsAuthenticated
                ? GetCurrentSessionKey()
                : null;

            // Token rotation raises the same event as login/logout. Keep the currently
            // displayed authoritative state when the authenticated account did not change.
            if (string.Equals(_observedSessionKey, currentSessionKey, StringComparison.Ordinal))
                return;

            _sessionGeneration++;
            _observedSessionKey = currentSessionKey;
            ClearAdBlockingState();
            InitializeUserData();
        }

        if (_dispatcher.CheckAccess())
            ApplySessionState();
        else
            _dispatcher.Invoke(ApplySessionState);
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
        CurrentPlan = _accountPlanService.CurrentPlanLabel;
        IsOpenVpnAvailable = _accountPlanService.IsOpenVpnAvailable;
        IsProPlan = _accountPlanService.IsPro;

        if (IsProPlan)
            return;

        if (DefaultProtocol == VpnProtocol.OpenVPN)
            DefaultProtocol = VpnProtocol.IKEv2;

        if (AutoConnect)
            AutoConnect = false;

        if (KillSwitch)
            KillSwitch = false;
    }

    private bool CanExecuteAdBlockingToggle() => CanToggleAdBlocking;

    private async Task LoadAdBlockingPreferenceAsync(
        string sessionKey,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        var lockTaken = false;
        string? previousStatus = null;

        try
        {
            await _adBlockingOperationLock.WaitAsync(cancellationToken);
            lockTaken = true;
            previousStatus = AdBlockingStatus;

            if (!IsCurrentSession(sessionKey, sessionGeneration))
                return;

            IsAdBlockingBusy = true;
            if (!HasAdBlockingPreference)
                AdBlockingStatus = "Loading ad blocking settings...";

            var preference = await _dnsSettingsService.GetPreferenceAsync(cancellationToken);
            if (!IsCurrentSession(sessionKey, sessionGeneration))
                return;

            if (preference is null)
            {
                AdBlockingStatus = "Unable to load ad blocking settings. Try again.";
                return;
            }

            ApplyAdBlockingPreference(preference, sessionKey, includePropagation: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (previousStatus is not null && IsCurrentSession(sessionKey, sessionGeneration))
                AdBlockingStatus = previousStatus;
        }
        catch
        {
            if (IsCurrentSession(sessionKey, sessionGeneration))
                AdBlockingStatus = "Unable to load ad blocking settings. Try again.";
        }
        finally
        {
            if (IsCurrentSession(sessionKey, sessionGeneration))
                IsAdBlockingBusy = false;

            if (lockTaken)
                _adBlockingOperationLock.Release();
        }
    }

    private async Task ToggleAdBlockingAsync(CancellationToken cancellationToken)
    {
        if (!CanToggleAdBlocking)
            return;

        var lockTaken = false;
        var previousStatus = AdBlockingStatus;
        var sessionKey = GetCurrentSessionKey();
        var sessionGeneration = _sessionGeneration;

        try
        {
            lockTaken = await _adBlockingOperationLock.WaitAsync(0, cancellationToken);
            if (!lockTaken || !CanToggleAdBlocking || !IsCurrentSession(sessionKey, sessionGeneration))
                return;

            var requestedValue = !AdBlockingRequestedEnabled;
            IsAdBlockingBusy = true;
            AdBlockingStatus = requestedValue
                ? "Enabling ad blocking..."
                : "Disabling ad blocking...";

            var result = await _dnsSettingsService.SetAdBlockingAsync(requestedValue, cancellationToken);
            if (!IsCurrentSession(sessionKey, sessionGeneration))
                return;

            if (result.Success && result.Preference is not null)
            {
                ApplyAdBlockingPreference(result.Preference, sessionKey, includePropagation: true);
                return;
            }

            if (string.Equals(result.ErrorCode, "PRO_REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                _adBlockingRequiresUpgrade = true;
                NotifyAdBlockingDerivedStateChanged();
                AdBlockingStatus = "Ad blocking requires Pro. Upgrade to change this setting.";
                return;
            }

            AdBlockingStatus = BuildRetryStatus(result.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentSession(sessionKey, sessionGeneration))
                AdBlockingStatus = previousStatus;
        }
        catch
        {
            if (IsCurrentSession(sessionKey, sessionGeneration))
                AdBlockingStatus = "Unable to save ad blocking settings. Try again.";
        }
        finally
        {
            if (IsCurrentSession(sessionKey, sessionGeneration))
                IsAdBlockingBusy = false;

            if (lockTaken)
                _adBlockingOperationLock.Release();
        }
    }

    private void ApplyAdBlockingPreference(
        DnsPreferenceResponse preference,
        string sessionKey,
        bool includePropagation)
    {
        _adBlockingPreferenceOwnerKey = sessionKey;
        _adBlockingRequiresUpgrade = false;
        _planDowngradeObservedAfterDnsLoad = false;
        HasAdBlockingPreference = true;
        AdBlockingRequestedEnabled = preference.RequestedEnabled;
        CanUseAdBlocking = preference.CanUseAdBlocking;
        AdBlockingEffectiveEnabled = preference.EffectiveEnabled;
        AdBlockingEffectiveMode = preference.EffectiveMode ?? string.Empty;
        AdBlockingPropagationSeconds = Math.Max(0, preference.PropagationSeconds);
        AdBlockingStatus = BuildAdBlockingStatus(includePropagation);
        NotifyAdBlockingDerivedStateChanged();
    }

    private void ClearAdBlockingState()
    {
        _adBlockingPreferenceOwnerKey = null;
        _adBlockingRequiresUpgrade = false;
        _planDowngradeObservedAfterDnsLoad = false;
        HasAdBlockingPreference = false;
        AdBlockingRequestedEnabled = false;
        CanUseAdBlocking = false;
        AdBlockingEffectiveEnabled = false;
        AdBlockingEffectiveMode = string.Empty;
        AdBlockingPropagationSeconds = 0;
        IsAdBlockingBusy = false;
        AdBlockingStatus = string.Empty;
        NotifyAdBlockingDerivedStateChanged();
    }

    private string BuildAdBlockingStatus(bool includePropagation)
    {
        string status;

        if (AdBlockingRequestedEnabled)
        {
            if (!CanUseAdBlocking || _adBlockingRequiresUpgrade || _planDowngradeObservedAfterDnsLoad)
                status = "Saved, paused until Pro is restored.";
            else if (AdBlockingEffectiveEnabled)
                status = "On. Ads and known tracking domains are filtered.";
            else
                status = "Saved, temporarily unavailable.";
        }
        else if (!CanUseAdBlocking || _adBlockingRequiresUpgrade || _planDowngradeObservedAfterDnsLoad)
        {
            status = "DNS ad blocking is available with Pro.";
        }
        else if (AdBlockingEffectiveEnabled)
        {
            status = "Off is saved; filtering is still being disabled.";
        }
        else
        {
            status = "Off. LibreGuard private DNS remains active.";
        }

        if (!includePropagation)
            return status;

        return AdBlockingPropagationSeconds == 0
            ? $"{status} Active connections update immediately."
            : $"{status} Active connections update within {AdBlockingPropagationSeconds} " +
              (AdBlockingPropagationSeconds == 1 ? "second." : "seconds.");
    }

    private static string BuildRetryStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Unable to save ad blocking settings. Try again.";

        var trimmed = message.Trim();
        return trimmed.Contains("try again", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed} Try again.";
    }

    private string GetCurrentSessionKey() =>
        $"{_authService.UserId ?? string.Empty}\u001f{_authService.UserEmail ?? string.Empty}";

    private bool IsCurrentSession(string sessionKey, long sessionGeneration) =>
        sessionGeneration == _sessionGeneration &&
        _authService.IsAuthenticated &&
        string.Equals(sessionKey, GetCurrentSessionKey(), StringComparison.Ordinal);

    private void NotifyAdBlockingDerivedStateChanged()
    {
        OnPropertyChanged(nameof(IsAdBlockingUpgradeRequired));
        OnPropertyChanged(nameof(CanToggleAdBlocking));
        OnPropertyChanged(nameof(AdBlockingActionText));
        ToggleAdBlockingCommand?.NotifyCanExecuteChanged();
    }

    private async Task DisableTwoFactorAsync()
    {
        try
        {
            var response = await _authService.DisableTwoFactorAsync();
            if (response != null)
            {
                _twoFactorAuth = false;
                OnPropertyChanged(nameof(TwoFactorAuth));
            }
            else
            {
                // Revert on failure
                _twoFactorAuth = true;
                OnPropertyChanged(nameof(TwoFactorAuth));
            }
        }
        catch
        {
            // Revert on failure
            _twoFactorAuth = true;
            OnPropertyChanged(nameof(TwoFactorAuth));
        }
    }

    private void OnTwoFactorAuthChanged(bool value)
    {
        if (value)
        {
            ShowTwoFactorSetupDialog?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            DisableTwoFactorDialog?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OpenPrivacyPolicy()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://libreguard.net/Privacy",
            UseShellExecute = true
        });
    }

    private void OpenTermsOfService()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://libreguard.net/Terms",
            UseShellExecute = true
        });
    }

    private void OpenHelpAndSupport()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://libreguard.net/Support",
            UseShellExecute = true
        });
    }

    private void OpenSourceLicenses()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/LibreGuard-Developer/libreguard-vpn-windows",
            UseShellExecute = true
        });
    }

    private void OpenManageAccount()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://management.libreguard.net/Identity/Account/Login",
            UseShellExecute = true
        });
    }
}
