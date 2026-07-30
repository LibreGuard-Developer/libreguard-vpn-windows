using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreGuard_VPN_Desktop.Services;
using System.Text.RegularExpressions;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Handles the forgot password logic: sending the reset email.
/// </summary>
public sealed partial class ForgotPasswordViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;
    private string? _resetSuccessMessage;
    private bool _isInResetFlow;
    private string _resetPasswordToken = string.Empty;
    private string _resetNewPassword = string.Empty;
    private string _resetConfirmPassword = string.Empty;
    private bool _isResetNewPasswordVisible;
    private bool _isResetConfirmPasswordVisible;
    private PasswordStrengthValidator _resetPasswordStrength = new();

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSuccess;

    [ObservableProperty]
    private string? _errorMessage;

    public string? ResetSuccessMessage
    {
        get => _resetSuccessMessage;
        private set => SetProperty(ref _resetSuccessMessage, value);
    }

    public bool IsInResetFlow
    {
        get => _isInResetFlow;
        private set
        {
            if (SetProperty(ref _isInResetFlow, value))
            {
                SubmitResetPasswordCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ResetPasswordToken
    {
        get => _resetPasswordToken;
        private set
        {
            if (SetProperty(ref _resetPasswordToken, value))
            {
                SubmitResetPasswordCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ResetNewPassword
    {
        get => _resetNewPassword;
        set
        {
            if (SetProperty(ref _resetNewPassword, value))
            {
                ValidatePassword(value);
                OnPropertyChanged(nameof(IsPasswordMatch));
                OnPropertyChanged(nameof(IsPasswordEmpty));
                SubmitResetPasswordCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ResetConfirmPassword
    {
        get => _resetConfirmPassword;
        set
        {
            if (SetProperty(ref _resetConfirmPassword, value))
            {
                OnPropertyChanged(nameof(IsPasswordMatch));
                SubmitResetPasswordCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsResetNewPasswordVisible
    {
        get => _isResetNewPasswordVisible;
        set => SetProperty(ref _isResetNewPasswordVisible, value);
    }

    public bool IsResetConfirmPasswordVisible
    {
        get => _isResetConfirmPasswordVisible;
        set => SetProperty(ref _isResetConfirmPasswordVisible, value);
    }

    public PasswordStrengthValidator ResetPasswordStrength
    {
        get => _resetPasswordStrength;
        private set
        {
            if (SetProperty(ref _resetPasswordStrength, value))
            {
                SubmitResetPasswordCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsPasswordEmpty => string.IsNullOrEmpty(ResetNewPassword);

    public bool IsPasswordMatch => !string.IsNullOrEmpty(ResetNewPassword) && ResetNewPassword == ResetConfirmPassword;

    public bool CanSubmitResetPassword =>
        IsInResetFlow &&
        !IsLoading &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(ResetPasswordToken) &&
        ResetPasswordStrength.IsValid &&
        IsPasswordMatch;

    public event EventHandler? NavigateBackToLogin;

    public ForgotPasswordViewModel(IAuthenticationService authService)
    {
        _authService = authService;
        SubmitResetPasswordCommand = new AsyncRelayCommand(ResetPasswordAsync, () => CanSubmitResetPassword);
        ToggleResetNewPasswordVisibilityCommand = new RelayCommand(ToggleNewPasswordVisibility);
        ToggleResetConfirmPasswordVisibilityCommand = new RelayCommand(ToggleConfirmPasswordVisibility);
    }

    partial void OnEmailChanged(string value) => SubmitResetPasswordCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingChanged(bool value) => SubmitResetPasswordCommand.NotifyCanExecuteChanged();

    public IAsyncRelayCommand SubmitResetPasswordCommand { get; }

    public IRelayCommand ToggleResetNewPasswordVisibilityCommand { get; }

    public IRelayCommand ToggleResetConfirmPasswordVisibilityCommand { get; }

    private void ValidatePassword(string password)
    {
        ResetPasswordStrength = new PasswordStrengthValidator
        {
            HasMinimumLength = password.Length >= 8,
            HasUpperCase = Regex.IsMatch(password, @"[A-Z]"),
            HasLowerCase = Regex.IsMatch(password, @"[a-z]"),
            HasDigit = Regex.IsMatch(password, @"[0-9]"),
            HasSpecialChar = Regex.IsMatch(password, @"[^a-zA-Z0-9\s]")
        };
    }

    /// <summary>
    /// Prepares the view model for a deep-linked password reset.
    /// </summary>
    public void StartResetFlow(string email, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        ResetFormState();
        Email = email;
        ResetPasswordToken = token;
        IsInResetFlow = true;
    }

    /// <summary>
    /// Returns the view model to the email-entry password reset state.
    /// </summary>
    public void ShowForgotPasswordForm()
    {
        ResetFormState();
        IsInResetFlow = false;
    }

    [RelayCommand]
    private async Task SendResetLinkAsync()
    {
        if (string.IsNullOrWhiteSpace(Email))
            return;

        IsLoading = true;
        ErrorMessage = null;
        IsSuccess = false;
        ResetSuccessMessage = null;

        try
        {
            await _authService.ForgotPasswordAsync(Email);
            IsSuccess = true;
            ResetSuccessMessage = "Check your email for reset instructions.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ResetPasswordAsync()
    {
        ErrorMessage = null;
        IsSuccess = false;
        ResetSuccessMessage = null;
        IsLoading = true;

        try
        {
            var result = await _authService.ResetPasswordAsync(Email, ResetPasswordToken, ResetNewPassword);
            if (result.Success)
            {
                IsSuccess = true;
                ResetSuccessMessage = result.Message ?? "Password has been reset successfully.";
                ResetNewPassword = string.Empty;
                ResetConfirmPassword = string.Empty;
                IsResetNewPasswordVisible = false;
                IsResetConfirmPasswordVisible = false;
            }
            else
            {
                ErrorMessage = result.Errors is { Count: > 0 }
                    ? string.Join(Environment.NewLine, result.Errors)
                    : result.Message ?? "Failed to reset password.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ToggleNewPasswordVisibility() => IsResetNewPasswordVisible = !IsResetNewPasswordVisible;

    private void ToggleConfirmPasswordVisibility() => IsResetConfirmPasswordVisible = !IsResetConfirmPasswordVisible;

    [RelayCommand]
    private void GoBack()
    {
        ShowForgotPasswordForm();
        NavigateBackToLogin?.Invoke(this, EventArgs.Empty);
    }

    private void ResetFormState()
    {
        ErrorMessage = null;
        IsSuccess = false;
        ResetSuccessMessage = null;
        ResetPasswordToken = string.Empty;
        ResetNewPassword = string.Empty;
        ResetConfirmPassword = string.Empty;
        IsResetNewPasswordVisible = false;
        IsResetConfirmPasswordVisible = false;
    }
}
