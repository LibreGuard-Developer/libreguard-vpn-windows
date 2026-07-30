using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreGuard_VPN_Desktop.Services;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Handles new account registration.
/// </summary>
public sealed partial class RegisterViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private bool _isPasswordVisible;

    [ObservableProperty]
    private bool _isConfirmPasswordVisible;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private PasswordStrengthValidator _passwordStrength = new();

    public bool IsPasswordEmpty => string.IsNullOrEmpty(Password);

    public bool IsPasswordMatch => !string.IsNullOrEmpty(Password) && Password == ConfirmPassword;

    public bool CanRegister => PasswordStrength.IsValid && IsPasswordMatch && !string.IsNullOrWhiteSpace(Email) && !IsLoading;

    public event EventHandler<(string Email, string Password)>? RegisterSucceeded;
    public event EventHandler? NavigateToLogin;

    public RegisterViewModel(IAuthenticationService authService)
    {
        _authService = authService;
    }

    partial void OnEmailChanged(string value) => RegisterCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingChanged(bool value) => RegisterCommand.NotifyCanExecuteChanged();

    partial void OnPasswordChanged(string value)
    {
        ValidatePassword(value);
        OnPropertyChanged(nameof(IsPasswordMatch));
        OnPropertyChanged(nameof(IsPasswordEmpty));
        RegisterCommand.NotifyCanExecuteChanged();
    }

    partial void OnConfirmPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(IsPasswordMatch));
        RegisterCommand.NotifyCanExecuteChanged();
    }

    private void ValidatePassword(string password)
    {
        PasswordStrength = new PasswordStrengthValidator
        {
            HasMinimumLength = password.Length >= 8,
            HasUpperCase = Regex.IsMatch(password, @"[A-Z]"),
            HasLowerCase = Regex.IsMatch(password, @"[a-z]"),
            HasDigit = Regex.IsMatch(password, @"[0-9]"),
            HasSpecialChar = Regex.IsMatch(password, @"[^a-zA-Z0-9\s]")
        };
    }

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private async Task RegisterAsync()
    {
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var result = await _authService.RegisterAsync(Email, Password);
            if (result.Success || result.RequiresEmailConfirmation)
            {
                RegisterSucceeded?.Invoke(this, (Email, Password));
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Registration failed.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void GoToLogin() => NavigateToLogin?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void TogglePasswordVisibility() => IsPasswordVisible = !IsPasswordVisible;

    [RelayCommand]
    private void ToggleConfirmPasswordVisibility() => IsConfirmPasswordVisible = !IsConfirmPasswordVisible;

    [RelayCommand]
    private void OpenPrivacyPolicy()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://libreguard.net/Privacy",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenTermsOfService()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://libreguard.net/Terms",
            UseShellExecute = true
        });
    }
}
