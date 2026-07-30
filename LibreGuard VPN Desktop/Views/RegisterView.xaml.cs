using System.Windows.Controls;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Views;

/// <summary>
/// Registration view for new user account creation.
/// </summary>
public partial class RegisterView : UserControl
{
    public RegisterView()
    {
        InitializeComponent();
        
        // Sync PasswordBox and ConfirmPasswordBox with ViewModel
        PasswordBox.PasswordChanged += (s, e) =>
        {
            if (DataContext is RegisterViewModel vm)
                vm.Password = PasswordBox.Password;
        };

        ConfirmPasswordBox.PasswordChanged += (s, e) =>
        {
            if (DataContext is RegisterViewModel vm)
                vm.ConfirmPassword = ConfirmPasswordBox.Password;
        };

        // When ViewModel properties change (e.g. from visible TextBoxes), sync back to PasswordBoxes
        DataContextChanged += (s, e) =>
        {
            if (e.NewValue is RegisterViewModel vm)
            {
                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(RegisterViewModel.Password))
                    {
                        if (PasswordBox.Password != vm.Password)
                            PasswordBox.Password = vm.Password;
                    }
                    else if (args.PropertyName == nameof(RegisterViewModel.ConfirmPassword))
                    {
                        if (ConfirmPasswordBox.Password != vm.ConfirmPassword)
                            ConfirmPasswordBox.Password = vm.ConfirmPassword;
                    }
                };
            }
        };
    }
}
