using System.Windows;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Views;

public partial class TwoFactorDisableConfirmationWindow : Window
{
    public TwoFactorDisableConfirmationWindow(TwoFactorDisableConfirmationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += (s, result) => 
        {
            DialogResult = result;
            Close();
        };
    }
}
