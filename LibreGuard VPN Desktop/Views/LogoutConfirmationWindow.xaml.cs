using System.Windows;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Views;

public partial class LogoutConfirmationWindow : Window
{
    public LogoutConfirmationWindow(LogoutConfirmationViewModel viewModel)
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
