using System.Windows;
using System.Windows.Controls;
using LibreGuard_VPN_Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LibreGuard_VPN_Desktop.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.ShowTwoFactorSetupDialog += OnShowTwoFactorSetupDialog;
            _viewModel.DisableTwoFactorDialog += OnDisableTwoFactorDialog;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.ShowTwoFactorSetupDialog -= OnShowTwoFactorSetupDialog;
            _viewModel.DisableTwoFactorDialog -= OnDisableTwoFactorDialog;
        }
    }

    private void OnShowTwoFactorSetupDialog(object? sender, EventArgs e)
    {
        try
        {
            var services = ((App)Application.Current).Services;
            if (services == null)
            {
                MessageBox.Show("Service provider is not initialized.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (_viewModel == null) return;

            var setupViewModel = services.GetRequiredService<TwoFactorSetupModalViewModel>();
            var window = new TwoFactorSetupWindow(setupViewModel)
            {
                Owner = Window.GetWindow(this)
            };

            if (window.ShowDialog() != true)
            {
                // If cancelled, revert the toggle
                _viewModel.RefreshUserDataAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open 2FA setup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            _viewModel?.RefreshUserDataAsync().ConfigureAwait(false);
        }
    }

    private async void OnDisableTwoFactorDialog(object? sender, EventArgs e)
    {
        if (_viewModel == null) return;

        try
        {
            var services = ((App)Application.Current).Services;
            if (services == null) return;

            var confirmationViewModel = services.GetRequiredService<TwoFactorDisableConfirmationViewModel>();
            var window = new TwoFactorDisableConfirmationWindow(confirmationViewModel)
            {
                Owner = Window.GetWindow(this)
            };

            if (window.ShowDialog() == true)
            {
                await _viewModel.DisableTwoFactorCommand.ExecuteAsync(null);
            }
            else
            {
                // Revert the toggle
                await _viewModel.RefreshUserDataAsync();
            }
        }
        catch (Exception)
        {
            // Revert the toggle on error
            await _viewModel.RefreshUserDataAsync();
        }
    }
}
