using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// ViewModel for the Logout confirmation dialog.
/// </summary>
public sealed partial class LogoutConfirmationViewModel : ObservableObject
{
    public event EventHandler<bool>? RequestClose;

    [RelayCommand]
    private void Confirm()
    {
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(this, false);
    }
}
