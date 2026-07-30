using System.Windows.Controls;
using System.Windows.Input;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    private async void StatusCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.Dashboard.ToggleConnectionCommand.CanExecute(null))
        {
            await viewModel.Dashboard.ToggleConnectionCommand.ExecuteAsync(null);
        }

        e.Handled = true;
    }
}
