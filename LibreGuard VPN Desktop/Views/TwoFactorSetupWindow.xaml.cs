using System.Windows;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Views;

public partial class TwoFactorSetupWindow : Window
{
    public TwoFactorSetupWindow(TwoFactorSetupModalViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SetupView.DataContext = viewModel;

        viewModel.SetupCompleted += (s, e) => {
            DialogResult = true;
            Close();
        };
        viewModel.SetupCancelled += (s, e) => {
            DialogResult = false;
            Close();
        };

        Loaded += async (s, e) => await viewModel.InitializeAsync();
    }
}
