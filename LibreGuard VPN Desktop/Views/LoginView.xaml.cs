using System.Windows.Controls;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        
        // Sync PasswordBox with ViewModel
        PasswordBox.PasswordChanged += (s, e) =>
        {
            if (DataContext is LoginViewModel vm)
            {
                vm.Password = PasswordBox.Password;
            }
        };

        // Sync back when VM property changes (e.g. from visible TextBox)
        DataContextChanged += (s, e) =>
        {
            if (e.NewValue is LoginViewModel vm)
            {
                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(LoginViewModel.Password))
                    {
                        if (PasswordBox.Password != vm.Password)
                        {
                            PasswordBox.Password = vm.Password;
                        }
                    }
                };
            }
        };
    }
}
