using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// ViewModel for the 2FA disable confirmation dialog.
/// </summary>
public sealed partial class TwoFactorDisableConfirmationViewModel : ObservableObject
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
