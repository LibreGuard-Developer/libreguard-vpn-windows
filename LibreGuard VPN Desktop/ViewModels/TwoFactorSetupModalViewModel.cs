using System.Linq;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;
using QRCoder;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Handles 2FA setup: generating QR codes, verifying TOTP codes, and enabling 2FA.
/// </summary>
public sealed partial class TwoFactorSetupModalViewModel : ObservableObject
{
    private readonly IAuthenticationService _authService;

    [ObservableProperty]
    private BitmapImage? qrCodeImage;

    [ObservableProperty]
    private string? sharedKey;

    [ObservableProperty]
    private string? backupCodes;

    [ObservableProperty]
    private ObservableCollection<string> recoveryCodeItems = new();

    [ObservableProperty]
    private bool hasRecoveryCodes;

    [ObservableProperty]
    private string? recoveryCodesMessage;

    [ObservableProperty]
    private string verificationCode = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string? successMessage;

    [ObservableProperty]
    private bool showBackupCodes;

    public event EventHandler? SetupCompleted;
    public event EventHandler? SetupCancelled;

    public TwoFactorSetupModalViewModel(IAuthenticationService authService)
    {
        _authService = authService;
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        SuccessMessage = null;
        BackupCodes = null;
        RecoveryCodeItems.Clear();
        HasRecoveryCodes = false;
        RecoveryCodesMessage = null;
        ShowBackupCodes = false;
        VerificationCode = string.Empty;

        try
        {
            var response = await _authService.InitiateTwoFactorSetupAsync();
            if (response is not null && !string.IsNullOrEmpty(response.AuthenticatorUri))
            {
                SharedKey = response.SharedKey;
                
                // Generate QR code from the authenticator URI
                GenerateQrCode(response.AuthenticatorUri);
            }
            else
            {
                ErrorMessage = "Failed to initiate 2FA setup. Please try again.";
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

    [RelayCommand]
    private async Task VerifyAndEnableAsync()
    {
        if (string.IsNullOrWhiteSpace(VerificationCode))
        {
            ErrorMessage = "Please enter the verification code.";
            return;
        }

        ErrorMessage = null;
        IsLoading = true;

        try
        {
            var response = await _authService.VerifyAndEnableTwoFactorAsync(VerificationCode);
            if (response is not null)
            {
                var recoveryCodes = await GetRecoveryCodesAsync(response.RecoveryCodes);
                ShowBackupCodes = true;
                if (recoveryCodes.Length > 0)
                {
                    ApplyRecoveryCodes(recoveryCodes);
                    RecoveryCodesMessage = response.Message ?? "Your authenticator app has been verified.";
                }
                else
                {
                    HasRecoveryCodes = false;
                    BackupCodes = null;
                    RecoveryCodesMessage = "Recovery codes were not returned. Use the button below to generate a new set.";
                }

                SuccessMessage = response.Message ?? "Two-factor authentication has been successfully enabled!";
            }
            else
            {
                ErrorMessage = "Verification failed. Please check your code and try again.";
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

    [RelayCommand]
    private void ToggleBackupCodes()
    {
        ShowBackupCodes = !ShowBackupCodes;
    }

    [RelayCommand]
    private void CopyRecoveryCodes()
    {
        if (!HasRecoveryCodes)
        {
            ErrorMessage = "There are no recovery codes to copy.";
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, RecoveryCodeItems));
        SuccessMessage = "Recovery codes copied to clipboard.";
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task GenerateRecoveryCodesAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var response = await _authService.GenerateRecoveryCodesAsync();
            var recoveryCodes = response?.RecoveryCodes?
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToArray()
                ?? Array.Empty<string>();

            if (recoveryCodes.Length == 0)
            {
                RecoveryCodesMessage = "The server did not return any recovery codes.";
                HasRecoveryCodes = false;
                BackupCodes = null;
                return;
            }

            ApplyRecoveryCodes(recoveryCodes);
            RecoveryCodesMessage = response?.Message ?? "New recovery codes generated.";
            ShowBackupCodes = true;
            SuccessMessage = RecoveryCodesMessage;
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

    [RelayCommand]
    private void Cancel()
    {
        if (ShowBackupCodes)
        {
            SetupCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetupCancelled?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string[]> GetRecoveryCodesAsync(string[]? responseCodes)
    {
        var recoveryCodes = responseCodes?
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToArray();

        if (recoveryCodes is { Length: > 0 })
        {
            return recoveryCodes;
        }

        var generatedCodesResponse = await _authService.GenerateRecoveryCodesAsync();
        return generatedCodesResponse?.RecoveryCodes?
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToArray()
            ?? Array.Empty<string>();
    }

    private void ApplyRecoveryCodes(string[] recoveryCodes)
    {
        RecoveryCodeItems.Clear();
        foreach (var code in recoveryCodes)
        {
            RecoveryCodeItems.Add(code);
        }

        HasRecoveryCodes = RecoveryCodeItems.Count > 0;
        BackupCodes = HasRecoveryCodes
            ? string.Join(Environment.NewLine, RecoveryCodeItems)
            : null;
    }

    private void GenerateQrCode(string authenticatorUri)
    {
        if (string.IsNullOrEmpty(authenticatorUri))
            return;

        try
        {
            var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(
                authenticatorUri,
                QRCodeGenerator.ECCLevel.Q);

            var qrCode = new PngByteQRCode(qrCodeData);
            var qrCodeBytes = qrCode.GetGraphic(10);

            QrCodeImage = ConvertBytesToImage(qrCodeBytes);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to generate QR code: {ex.Message}";
        }
    }

    private static BitmapImage ConvertBytesToImage(byte[] imageBytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new System.IO.MemoryStream(imageBytes);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
