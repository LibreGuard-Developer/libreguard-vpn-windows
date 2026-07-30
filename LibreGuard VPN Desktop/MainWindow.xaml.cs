using System;
using System.Windows;
using System.ComponentModel;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LibreGuard_VPN_Desktop;

/// <summary>
/// Application shell window — hosts sidebar and content views.
/// </summary>
public partial class MainWindow : Window
{
    private ForgotPasswordViewModel? _forgotPasswordViewModel;
    private readonly VpnShutdownService _vpnShutdownService;
    private bool _shutdownDisconnectStarted;
    private bool _shutdownDisconnectCompleted;

    public MainWindow(MainViewModel viewModel, VpnShutdownService vpnShutdownService)
    {
        ArgumentNullException.ThrowIfNull(vpnShutdownService);

        InitializeComponent();
        DataContext = viewModel;
        _vpnShutdownService = vpnShutdownService;
        _forgotPasswordViewModel = viewModel.ForgotPassword;

        if (FindName("ResetPasswordBox") is System.Windows.Controls.PasswordBox resetPasswordBox)
        {
            resetPasswordBox.PasswordChanged += OnResetPasswordChanged;
        }

        if (FindName("ResetConfirmPasswordBox") is System.Windows.Controls.PasswordBox resetConfirmPasswordBox)
        {
            resetConfirmPasswordBox.PasswordChanged += OnResetConfirmPasswordChanged;
        }

        viewModel.LogoutConfirmationRequested += OnLogoutConfirmationRequested;
        viewModel.AppShutdownRequested += OnAppShutdownRequested;
        Closing += OnClosing;

        Closed += (_, _) =>
        {
            Closing -= OnClosing;
            viewModel.AppShutdownRequested -= OnAppShutdownRequested;

            if (FindName("ResetPasswordBox") is System.Windows.Controls.PasswordBox resetPasswordBox)
            {
                resetPasswordBox.PasswordChanged -= OnResetPasswordChanged;
            }

            if (FindName("ResetConfirmPasswordBox") is System.Windows.Controls.PasswordBox resetConfirmPasswordBox)
            {
                resetConfirmPasswordBox.PasswordChanged -= OnResetConfirmPasswordChanged;
            }
        };
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDisconnectCompleted)
            return;

        e.Cancel = true;

        if (_shutdownDisconnectStarted)
            return;

        _shutdownDisconnectStarted = true;

        try
        {
            var status = await _vpnShutdownService.GetTunnelStatusAsync();
            if (status.ShouldWarnOnExit)
            {
                var result = MessageBox.Show(
                    "Exiting LibreGuard VPN will terminate the active VPN tunnel. Do you want to close the app and disconnect now?",
                    "Close LibreGuard VPN",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    _shutdownDisconnectStarted = false;
                    return;
                }
            }

            IsEnabled = false;
            var shutdownResult = await _vpnShutdownService.DisconnectOnExitAsync(status);
            if (!shutdownResult.Succeeded)
            {
                MessageBox.Show(
                    $"LibreGuard VPN could not verify that the VPN tunnel was closed, so the app will remain open.\n\n{shutdownResult.Message}",
                    "VPN Shutdown Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                IsEnabled = true;
                _shutdownDisconnectStarted = false;
                return;
            }

            _shutdownDisconnectCompleted = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"LibreGuard VPN could not close safely because tunnel shutdown failed.\n\n{ex.Message}",
                "VPN Shutdown Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            IsEnabled = true;
            _shutdownDisconnectStarted = false;
        }
    }

    private void OnResetPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_forgotPasswordViewModel is not null && sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _forgotPasswordViewModel.ResetNewPassword = passwordBox.Password;
        }
    }

    private void OnResetConfirmPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_forgotPasswordViewModel is not null && sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _forgotPasswordViewModel.ResetConfirmPassword = passwordBox.Password;
        }
    }

    private void OnLogoutConfirmationRequested(object? sender, EventArgs e)
    {
        var services = ((App)Application.Current).Services;
        if (services == null) return;

        var viewModel = services.GetRequiredService<LogoutConfirmationViewModel>();
        var window = new Views.LogoutConfirmationWindow(viewModel)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            if (DataContext is MainViewModel mainVm)
            {
                _ = mainVm.LogoutCommand.ExecuteAsync(null);
            }
        }
    }

    private void OnAppShutdownRequested(object? sender, EventArgs e)
    {
        Close();
    }
}
