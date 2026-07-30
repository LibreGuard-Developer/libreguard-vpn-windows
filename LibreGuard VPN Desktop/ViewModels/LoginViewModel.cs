using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Handles email/password login, Google login, 2FA, and pre-auth device recovery.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private enum PreAuthDeviceRemovalMode
    {
        None,
        Password,
        GoogleCode
    }

    private readonly IAuthenticationService _authService;
    private readonly ILoggerService _logger;
    private readonly IGoogleAuthService _googleAuthService;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _showPassword;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isGoogleSignInRunning;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isTwoFactorRequired;

    [ObservableProperty]
    private string _twoFactorCode = string.Empty;

    [ObservableProperty]
    private bool _isDeviceLimitReached;

    [ObservableProperty]
    private List<UserDeviceDto>? _devices;

    [ObservableProperty]
    private UserDeviceDto? _selectedDeviceToRemove;

    private PreAuthDeviceRemovalMode _preAuthDeviceRemovalMode;
    private string? _pendingLoginToken;
    private CancellationTokenSource? _loginCts;
    private CancellationTokenSource? _googleLoginCts;
    private CancellationTokenSource? _deviceRemovalCts;

    public event EventHandler? LoginSucceeded;
    public event EventHandler? NavigateToRegister;
    public event EventHandler? NavigateToForgotPassword;
    public event EventHandler<string>? EmailVerificationRequired;

    public LoginViewModel(
        IAuthenticationService authService,
        ILoggerService logger,
        IGoogleAuthService googleAuthService)
    {
        _authService = authService;
        _logger = logger;
        _googleAuthService = googleAuthService;
    }

    [RelayCommand]
    private void CancelLogin()
    {
        _logger.LogInformation("[LoginVM] CancelLogin called");
        _loginCts?.Cancel();
        ResetPendingAuthenticationState();
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
            return;

        ErrorMessage = null;
        IsLoading = true;
        ResetPendingAuthenticationState();
        _loginCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loginCts = cancellation;

        _logger.LogInformation("[LoginVM] Starting standard login flow");
        try
        {
            await ExecutePasswordLoginAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[LoginVM] Login was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError("[LoginVM] Login connection error", ex);
            ErrorMessage = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            if (ReferenceEquals(_loginCts, cancellation))
                _loginCts = null;
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private async Task VerifyTwoFactorAsync()
    {
        if (string.IsNullOrWhiteSpace(TwoFactorCode))
            return;

        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var result = await _authService.Verify2FaAsync(Email, TwoFactorCode, _pendingLoginToken);
            HandleAuthResult(result, _preAuthDeviceRemovalMode);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Verification error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CancelTwoFactor()
    {
        IsTwoFactorRequired = false;
        TwoFactorCode = string.Empty;
        ErrorMessage = null;
        _pendingLoginToken = null;
        _preAuthDeviceRemovalMode = PreAuthDeviceRemovalMode.None;
    }

    [RelayCommand]
    private async Task RemoveDeviceAsync()
    {
        if (SelectedDeviceToRemove is null)
            return;

        var selectedDeviceId = SelectedDeviceToRemove.Id;
        var removalMode = _preAuthDeviceRemovalMode;
        ErrorMessage = null;
        IsLoading = true;
        _deviceRemovalCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _deviceRemovalCts = cancellation;

        try
        {
            PreAuthDeviceRemovalResult removalResult;
            if (removalMode == PreAuthDeviceRemovalMode.GoogleCode)
            {
                IsGoogleSignInRunning = true;
                var removalContext = await _googleAuthService.LoginAsync(cancellation.Token);
                if (!removalContext.HasCompletionData)
                {
                    ErrorMessage = removalContext.ErrorMessage ?? "Google sign-in did not return an authorization code.";
                    return;
                }

                removalResult = await _authService.RemoveDevicePreAuthOAuthAsync(
                    removalContext,
                    "Google",
                    selectedDeviceId,
                    cancellation.Token);
            }
            else
            {
                removalResult = await _authService.RemoveDevicePreAuthAsync(
                    Email,
                    Password,
                    selectedDeviceId,
                    cancellation.Token);
            }

            if (!removalResult.Success)
            {
                ErrorMessage = FormatRemovalError(removalResult);
                return;
            }

            ClearDeviceLimitState(clearMode: false);
            if (removalMode == PreAuthDeviceRemovalMode.GoogleCode)
            {
                await ExecuteGoogleLoginAsync(cancellation.Token);
            }
            else
            {
                await ExecutePasswordLoginAsync(cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[LoginVM] Pre-auth device removal was cancelled.");
            ErrorMessage = "Device removal cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError("[LoginVM] Pre-auth device removal failed.", ex);
            ErrorMessage = $"Error removing device: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            IsGoogleSignInRunning = false;
            if (ReferenceEquals(_deviceRemovalCts, cancellation))
                _deviceRemovalCts = null;
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void CancelDeviceRemoval()
    {
        _deviceRemovalCts?.Cancel();
        ClearDeviceLimitState(clearMode: true);
        ErrorMessage = null;
    }

    [RelayCommand]
    private void CancelGoogleLogin()
    {
        _logger.LogInformation("[LoginVM] CancelGoogleLogin called manually");
        _googleLoginCts?.Cancel();
        _deviceRemovalCts?.Cancel();
        ResetPendingAuthenticationState();
    }

    [RelayCommand]
    private async Task LoginWithGoogleAsync()
    {
        ErrorMessage = null;
        IsLoading = true;
        IsGoogleSignInRunning = true;
        ResetPendingAuthenticationState();
        _googleLoginCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _googleLoginCts = cancellation;

        _logger.LogInformation("[LoginVM] Starting Google login flow");
        try
        {
            await ExecuteGoogleLoginAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[LoginVM] Google login cancelled by user.");
            ErrorMessage = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError("[LoginVM] Google login failure", ex);
            ErrorMessage = string.IsNullOrWhiteSpace(ex.Message) ? "Google login failed." : ex.Message;
        }
        finally
        {
            IsLoading = false;
            IsGoogleSignInRunning = false;
            if (ReferenceEquals(_googleLoginCts, cancellation))
                _googleLoginCts = null;
            cancellation.Dispose();
        }
    }

    private async Task ExecutePasswordLoginAsync(CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(Email, Password, cancellationToken);
        HandleAuthResult(result, PreAuthDeviceRemovalMode.Password);
    }

    private async Task ExecuteGoogleLoginAsync(CancellationToken cancellationToken)
    {
        var loginContext = await _googleAuthService.LoginAsync(cancellationToken);
        if (!loginContext.HasCompletionData)
        {
            _logger.LogWarning("[LoginVM] Google login returned incomplete authorization data.");
            ErrorMessage = loginContext.ErrorMessage ?? "Google sign-in did not return an authorization code.";
            return;
        }

        Email = loginContext.Email ?? Email;
        var result = await _authService.LoginWithGoogleAsync(loginContext, cancellationToken);
        HandleAuthResult(result, PreAuthDeviceRemovalMode.GoogleCode);
    }

    [RelayCommand]
    private void TogglePasswordVisibility() => ShowPassword = !ShowPassword;

    [RelayCommand]
    private void GoToRegister() => NavigateToRegister?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void GoToForgotPassword() => NavigateToForgotPassword?.Invoke(this, EventArgs.Empty);

    private void HandleAuthResult(AuthResult result, PreAuthDeviceRemovalMode authenticationMode)
    {
        if (result.Success)
        {
            IsTwoFactorRequired = false;
            ClearDeviceLimitState(clearMode: true);
            _pendingLoginToken = null;
            TwoFactorCode = string.Empty;
            LoginSucceeded?.Invoke(this, EventArgs.Empty);
        }
        else if (result.RequiresTwoFactor)
        {
            IsTwoFactorRequired = true;
            IsDeviceLimitReached = false;
            _preAuthDeviceRemovalMode = authenticationMode;
            _pendingLoginToken = result.PendingLoginToken;
            Email = result.Email ?? Email;
        }
        else if (result.RequiresEmailConfirmation)
        {
            _pendingLoginToken = null;
            _preAuthDeviceRemovalMode = PreAuthDeviceRemovalMode.None;
            EmailVerificationRequired?.Invoke(this, result.Email ?? Email);
        }
        else if (result.DeviceLimitExceeded)
        {
            IsTwoFactorRequired = false;
            _pendingLoginToken = null;
            _preAuthDeviceRemovalMode = authenticationMode;
            IsDeviceLimitReached = true;
            Devices = result.Devices;
            ErrorMessage = result.ErrorMessage;
        }
        else
        {
            _preAuthDeviceRemovalMode = PreAuthDeviceRemovalMode.None;
            ErrorMessage = result.ErrorMessage ?? "Login failed.";
        }
    }

    private void ResetPendingAuthenticationState()
    {
        IsTwoFactorRequired = false;
        ClearDeviceLimitState(clearMode: true);
        _pendingLoginToken = null;
    }

    private void ClearDeviceLimitState(bool clearMode)
    {
        IsDeviceLimitReached = false;
        Devices = null;
        SelectedDeviceToRemove = null;
        if (clearMode)
            _preAuthDeviceRemovalMode = PreAuthDeviceRemovalMode.None;
    }

    private static string FormatRemovalError(PreAuthDeviceRemovalResult result)
    {
        var message = result.Message ?? "Failed to remove device.";
        return result.RetryAfterSeconds is > 0
            ? $"{message} Try again in {result.RetryAfterSeconds} seconds."
            : message;
    }
}
