using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Top-level shell ViewModel: manages auth flow, sidebar navigation, and active content view.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;
    private readonly INavigationService _navigationService;
    private readonly Dispatcher _dispatcher;
    private readonly IGoogleAuthService _googleAuthService;
    private readonly IAccountPlanService _accountPlanService;

    public DashboardViewModel Dashboard { get; }

    public ServerListViewModel ServerList { get; }
    public StatisticsViewModel Statistics { get; }
    public SettingsViewModel Settings { get; }
    public LoginViewModel Login { get; }
    public RegisterViewModel Register { get; }
    public ForgotPasswordViewModel ForgotPassword { get; }
    public UpgradeViewModel Upgrade { get; }

    [ObservableProperty]
    private AuthScreen _currentAuthScreen = AuthScreen.Login;


    [ObservableProperty]
    private string _activeTab = "home";

    [ObservableProperty]
    private string? _registeredEmail;

    [ObservableProperty]
    private string? _registeredPassword;

    public MainViewModel(
        IAuthenticationService authService,
        INavigationService navigationService,
        DashboardViewModel dashboard,
        ServerListViewModel serverList,
        StatisticsViewModel statistics,
        SettingsViewModel settings,
        LoginViewModel login,
        RegisterViewModel register,
        ForgotPasswordViewModel forgotPassword,
        UpgradeViewModel upgrade,
        IGoogleAuthService googleAuthService,
        SingleInstanceService singleInstanceService,
        IUserSettingsService userSettingsService,
        IAccountPlanService accountPlanService)
    {
        _authService = authService;
        _navigationService = navigationService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _googleAuthService = googleAuthService;
        _accountPlanService = accountPlanService;

        Dashboard = dashboard;

        ServerList = serverList;
        Statistics = statistics;
        Settings = settings;
        Login = login;
        Register = register;
        ForgotPassword = forgotPassword;
        Upgrade = upgrade;

        singleInstanceService.DeepLinkReceived += (s, link) =>
        {
            _dispatcher.Invoke(() => 
            {
                HandleDeepLink(link);
                if (Application.Current.MainWindow != null)
                {
                    if (Application.Current.MainWindow.WindowState == WindowState.Minimized)
                    {
                        Application.Current.MainWindow.WindowState = WindowState.Normal;
                    }
                    Application.Current.MainWindow.Activate();
                    Application.Current.MainWindow.Topmost = true;
                    Application.Current.MainWindow.Topmost = false;
                    Application.Current.MainWindow.Focus();
                }
            });
        };

        Login.LoginSucceeded += async (_, _) =>
        {
            await HandleLoginSuccessAsync();
        };

        Login.NavigateToRegister += (_, _) => CurrentAuthScreen = AuthScreen.Register;
        Login.NavigateToForgotPassword += (_, _) =>
        {
            ForgotPassword.ShowForgotPasswordForm();
            CurrentAuthScreen = AuthScreen.ForgotPassword;
        };
        Login.EmailVerificationRequired += (_, email) =>
        {
            RegisteredEmail = email;
            RegisteredPassword = Login.Password;
            CurrentAuthScreen = AuthScreen.EmailConfirmation;
        };
        Register.RegisterSucceeded += (_, data) =>
        {
            RegisteredEmail = data.Email;
            RegisteredPassword = data.Password;
            CurrentAuthScreen = AuthScreen.EmailConfirmation;
        };
        Register.NavigateToLogin += (_, _) => CurrentAuthScreen = AuthScreen.Login;
        ForgotPassword.NavigateBackToLogin += (_, _) => CurrentAuthScreen = AuthScreen.Login;

        _navigationService.ViewChanged += (s, tab) =>

        {
            if (ActiveTab != tab)
            {
                ActiveTab = tab;
            }
        };

        _authService.SessionChanged += () =>
        {
            _dispatcher.Invoke(async () =>
            {
                if (!_authService.IsAuthenticated && CurrentAuthScreen == AuthScreen.Authenticated)
                {
                    CurrentAuthScreen = AuthScreen.Login;
                    ActiveTab = "home";
                }
                else if (_authService.IsAuthenticated && CurrentAuthScreen == AuthScreen.Authenticated)
                {
                    // If we just refreshed our session (e.g. background token rotation),
                    // make sure to refresh all UI data to avoid showing "0.00 GB" or stale data.
                    await RefreshAllDataAsync(retryPlanRefresh: false);
                }
            });
        };

        if (_authService.IsAuthenticated)
        {
            CurrentAuthScreen = AuthScreen.Authenticated;
            Dashboard.Plan = _accountPlanService.CurrentPlan;
            _ = RefreshAllDataAsync(retryPlanRefresh: false).ContinueWith(t =>
            {
                // Use Dashboard.Plan — refreshed from the subscription API inside RefreshAllDataAsync,
                // so it is more reliable than _authService.Plan which reads the cached token field.
                if (userSettingsService.Settings.AutoConnect && _accountPlanService.IsPro)
                {
                    _dispatcher.Invoke(() =>
                    {
                        _ = Dashboard.ToggleConnectionCommand.ExecuteAsync(null);
                    });
                }
            }, TaskContinuationOptions.None);
        }
    }

    private async Task RefreshAllDataAsync(bool retryPlanRefresh = false)
    {
        await ServerList.RefreshPlanAsync(retryPlanRefresh);
        await Task.WhenAll(
            Settings.RefreshUserDataAsync(retryPlanRefresh),
            Dashboard.RefreshDataAsync(),
            Statistics.RefreshAsync());
    }


    [RelayCommand]
    private async Task NavigateToTab(string tab)
    {
        ActiveTab = tab;
        _navigationService.NavigateTo(tab);
        if (tab == "servers")
        {
            await ServerList.RefreshServersAsync();
        }
        else if (tab == "settings")
        {
            await Settings.RefreshUserDataAsync();
        }
    }

    public event EventHandler? LogoutConfirmationRequested;
    public event EventHandler? AppShutdownRequested;

    [RelayCommand]
    private void RequestLogout()
    {
        LogoutConfirmationRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try 
        {
            await _authService.LogoutAsync();
            await _googleAuthService.LogoutAsync();
            CurrentAuthScreen = AuthScreen.Login;
            ActiveTab = "home";
            await Settings.RefreshUserDataAsync();
        }
        catch (Exception ex)
        {
            if (ex is InvalidOperationException || ex.Message.Contains("Session expired") || ex.Message.Contains("invalid"))
            {
                MessageBox.Show(ex.Message, "Sign Out", MessageBoxButton.OK, MessageBoxImage.Information);
                CurrentAuthScreen = AuthScreen.Login;
                ActiveTab = "home";
                await Settings.RefreshUserDataAsync();
            }
            else
            {
                MessageBox.Show(ex.Message, "Sign Out Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            System.Diagnostics.Debug.WriteLine($"Logout failed: {ex.Message}");
        }
    }


    [RelayCommand]
    private void BackToLogin() => CurrentAuthScreen = AuthScreen.Login;

    [RelayCommand]
    private async Task ConfirmEmailAsync()
    {
        if (string.IsNullOrEmpty(RegisteredEmail) || string.IsNullOrEmpty(RegisteredPassword))
        {
            CurrentAuthScreen = AuthScreen.Login;
            return;
        }

        try
        {
            var result = await _authService.LoginAsync(RegisteredEmail, RegisteredPassword);
            if (result.Success)
            {
                await HandleLoginSuccessAsync();
            }
            else if (result.RequiresEmailConfirmation)
            {
                MessageBox.Show("Your email is not verified yet. Please check your inbox and click the verification link.", "Email Not Verified", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(result.ErrorMessage ?? "Login failed.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                CurrentAuthScreen = AuthScreen.Login;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error verifying email: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void HandleDeepLink(string deepLink)
    {
        try
        {
            if (DeepLinkParser.TryParse(deepLink, out var payload))
            {
                if (payload.Action == DeepLinkAction.LoginWithToken && !string.IsNullOrWhiteSpace(payload.Token))
                {
                    _ = HandleDeepLinkLoginAsync(payload.Token);
                }
                else if (payload.Action == DeepLinkAction.ResetPassword
                    && !string.IsNullOrWhiteSpace(payload.Email)
                    && !string.IsNullOrWhiteSpace(payload.Token))
                {
                    ForgotPassword.StartResetFlow(payload.Email, payload.Token);
                    CurrentAuthScreen = AuthScreen.ForgotPassword;
                }
                else if (payload.Action == DeepLinkAction.Shutdown)
                {
                    AppShutdownRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error handling deep link: {ex.Message}");
        }
    }

    private async Task HandleDeepLinkLoginAsync(string token)
    {
        try
        {
            var result = await _authService.LoginWithTokenAsync(token);
            if (result.Success)
            {
                await HandleLoginSuccessAsync();
            }
            else
            {
                MessageBox.Show(result.ErrorMessage ?? "Failed to login with deep link.", "Login Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to login with deep link: {ex.Message}", "Login Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task HandleLoginSuccessAsync()
    {
        CurrentAuthScreen = AuthScreen.Authenticated;
        await _accountPlanService.RefreshAsync(force: true);
        Dashboard.Plan = _accountPlanService.CurrentPlan;
        await RefreshAllDataAsync(retryPlanRefresh: true);
        await RefreshAllDataAsync(retryPlanRefresh: true);
    }
}
