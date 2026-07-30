using System.Net;
using System.Net.Http;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Authentication service backed by the LibreGuard management API.
/// </summary>
internal sealed class ApiAuthenticationService : IAuthenticationService
{
    private readonly ApiHttpClientService _api;
    private readonly TokenStorageService _tokenStorage;
    private readonly ILoggerService _logger;
    private readonly DeviceKeyService _deviceKeyService;

    public ApiAuthenticationService(
        ApiHttpClientService api, 
        TokenStorageService tokenStorage,
        ILoggerService logger,
        DeviceKeyService deviceKeyService)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(tokenStorage);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(deviceKeyService);

        _api = api;
        _tokenStorage = tokenStorage;
        _logger = logger;
        _deviceKeyService = deviceKeyService;
    }

    public event Action? SessionChanged
    {
        add => _tokenStorage.SessionChanged += value;
        remove => _tokenStorage.SessionChanged -= value;
    }

    public bool IsAuthenticated => _tokenStorage.HasToken;
    public string? UserEmail => _tokenStorage.Email;
    public string? UserId => _tokenStorage.UserId;

    public UserPlan Plan
    {
        get
        {
            if (string.Equals(_tokenStorage.PlanType, "Pro", StringComparison.OrdinalIgnoreCase))
            {
                return UserPlan.Pro;
            }
            return UserPlan.Free;
        }
    }


    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var deviceKey = _deviceKeyService.GetRegistration();
        var body = new
        {
            email,
            password,
            deviceId = _tokenStorage.DeviceId,
            appVersion = AppVersionProvider.GetApiVersion(),
            devicePublicKey = deviceKey.DevicePublicKey,
            devicePublicKeyId = deviceKey.DevicePublicKeyId,
            devicePublicKeyAlgorithm = deviceKey.DevicePublicKeyAlgorithm
        };

        using var response = await _api.PostPublicRawAsync("api/login", body, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var login = await ApiHttpClientService.DeserializeAsync<LoginResponse>(response, cancellationToken);
            if (login is null)
                return AuthResult.Fail("Unexpected server response.");

            if (login.RequiresTwoFactor)
                return AuthResult.TwoFactorRequired(login.Email ?? email, login.UserId ?? string.Empty, login.PendingLoginToken);

            if (login.Token is not null && login.RefreshToken is not null)
            {
                _tokenStorage.StoreSession(
                    login.Token,
                    login.RefreshToken,
                    login.UserId ?? string.Empty,
                    login.Email ?? email,
                    login.PlanType);

                return AuthResult.Ok();
            }

            return AuthResult.Fail(login.Message ?? "Login failed.");
        }

        return await HandleErrorResponseAsync(response, cancellationToken);
    }

    public async Task<AuthResult> LoginWithTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var deviceKey = _deviceKeyService.GetRegistration();
            // We need to exchange the deep link token for a proper device-bound session
            var body = new
            {
                token,
                deviceId = _tokenStorage.DeviceId,
                appVersion = AppVersionProvider.GetApiVersion(),
                devicePublicKey = deviceKey.DevicePublicKey,
                devicePublicKeyId = deviceKey.DevicePublicKeyId,
                devicePublicKeyAlgorithm = deviceKey.DevicePublicKeyAlgorithm
            };

            using var response = await _api.PostPublicRawAsync("api/login/token", body, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var login = await ApiHttpClientService.DeserializeAsync<LoginResponse>(response, cancellationToken);
                if (login is null)
                    return AuthResult.Fail("Unexpected server response.");

                if (login.Token is not null && login.RefreshToken is not null)
                {
                    _tokenStorage.StoreSession(
                        login.Token,
                        login.RefreshToken,
                        login.UserId ?? string.Empty,
                        login.Email ?? string.Empty,
                        login.PlanType);

                    return AuthResult.Ok();
                }

                return AuthResult.Fail(login.Message ?? "Login failed.");
            }

            return await HandleErrorResponseAsync(response, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error during token login", ex);
            return AuthResult.Fail($"Connection error: {ex.Message}");
        }
    }

    public async Task<AuthResult> Verify2FaAsync(string email, string code, string? pendingLoginToken = null, CancellationToken cancellationToken = default)
    {
        var deviceKey = _deviceKeyService.GetRegistration();
        var body = new
        {
            email,
            twoFactorCode = code,
            pendingLoginToken,
            deviceId = _tokenStorage.DeviceId,
            appVersion = AppVersionProvider.GetApiVersion(),
            devicePublicKey = deviceKey.DevicePublicKey,
            devicePublicKeyId = deviceKey.DevicePublicKeyId,
            devicePublicKeyAlgorithm = deviceKey.DevicePublicKeyAlgorithm
        };

        using var response = await _api.PostPublicRawAsync("api/login/verify-2fa", body, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var login = await ApiHttpClientService.DeserializeAsync<LoginResponse>(response, cancellationToken);
            if (login?.Token is not null && login.RefreshToken is not null)
            {
                _tokenStorage.StoreSession(
                    login.Token,
                    login.RefreshToken,
                    login.UserId ?? string.Empty,
                    login.Email ?? email,
                    login.PlanType);

                return AuthResult.Ok();
            }

            return AuthResult.Fail("Verification failed.");
        }

        return await HandleErrorResponseAsync(response, cancellationToken);
    }

    public async Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var body = new { email, password };

        using var response = await _api.PostPublicRawAsync("api/register", body, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var reg = await ApiHttpClientService.DeserializeAsync<RegisterResponse>(response, cancellationToken);
            if (reg is null)
                return AuthResult.Fail("Unexpected server response.");

            if (reg.RequiresEmailConfirmation)
                return new AuthResult
                {
                    RequiresEmailConfirmation = true,
                    Email = reg.Email ?? email,
                    UserId = reg.UserId
                };

            return AuthResult.Ok();
        }

        return await HandleErrorResponseAsync(response, cancellationToken);
    }

    public async Task<bool> CheckEmailConfirmationAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _api.GetPublicAsync<EmailConfirmationStatusResponse>(
                $"api/register/check-confirmation/{userId}", cancellationToken);
            return result?.EmailConfirmed ?? false;
        }
        catch
        {
            return false;
        }
    }

    public async Task ResendConfirmationAsync(string email, CancellationToken cancellationToken = default)
    {
        using var response = await _api.PostPublicRawAsync(
            "api/register/resend-confirmation",
            new { email },
            cancellationToken);
        // The backend always returns 200 regardless of whether the email exists.
    }

    public async Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthAsync(string email, string password, int deviceIdToRemove, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _api.PostPublicRawAsync(
                "api/devices/pre-auth/remove",
                new { email, password, deviceIdToRemove },
                cancellationToken);
            return await HandlePreAuthDeviceRemovalResponseAsync(response, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[PreAuthDeviceRemoval] Password device removal failed.", ex);
            return PreAuthDeviceRemovalResult.Fail("Unable to remove the device. Please try again.");
        }
    }

    public async Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthOAuthAsync(GoogleLoginContext loginContext, string provider, int deviceIdToRemove, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(loginContext);

            if (string.IsNullOrWhiteSpace(loginContext.ClientId) ||
                string.IsNullOrWhiteSpace(loginContext.AuthorizationCode) ||
                string.IsNullOrWhiteSpace(loginContext.RedirectUri) ||
                string.IsNullOrWhiteSpace(loginContext.CodeVerifier))
            {
                return PreAuthDeviceRemovalResult.Fail(
                    loginContext.ErrorMessage ?? "Google sign-in did not return a complete authorization code.",
                    "INVALID_OAUTH_COMPLETION");
            }

            using var response = await _api.PostPublicRawAsync(
                "api/devices/pre-auth/oauth/remove-code",
                new
                {
                    provider,
                    clientId = loginContext.ClientId,
                    code = loginContext.AuthorizationCode,
                    redirectUri = loginContext.RedirectUri,
                    codeVerifier = loginContext.CodeVerifier,
                    deviceIdToRemove
                },
                cancellationToken);
            return await HandlePreAuthDeviceRemovalResponseAsync(response, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[PreAuthDeviceRemoval] Google device removal failed.", ex);
            return PreAuthDeviceRemovalResult.Fail("Unable to remove the device with Google. Please try again.");
        }
    }

    private static async Task<PreAuthDeviceRemovalResult> HandlePreAuthDeviceRemovalResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var success = await ApiHttpClientService.DeserializeAsync<PreAuthDeviceRemovalResponse>(response, cancellationToken);
                return success?.Success == true
                    ? PreAuthDeviceRemovalResult.Ok(success.Message ?? "Device removed successfully.")
                    : PreAuthDeviceRemovalResult.Fail(success?.Message ?? "The server returned an invalid device removal response.", "INVALID_RESPONSE");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return PreAuthDeviceRemovalResult.Fail("The server returned an invalid device removal response.", "INVALID_RESPONSE");
            }
        }

        ApiErrorResponse? error = null;
        try
        {
            error = await ApiHttpClientService.DeserializeAsync<ApiErrorResponse>(response, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall back to an HTTP status based message below.
        }

        var message = error?.Message ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Google authorization expired or was rejected. Please sign in again.",
            HttpStatusCode.NotFound => "The selected device was not found. Refresh the device list and try again.",
            HttpStatusCode.TooManyRequests => "Too many device removal attempts. Please wait and try again.",
            HttpStatusCode.BadRequest => "The device removal request was invalid.",
            _ => $"Device removal failed ({(int)response.StatusCode})."
        };

        return PreAuthDeviceRemovalResult.Fail(message, error?.ErrorCode, error?.RetryAfterSeconds);
    }

    private sealed record PreAuthDeviceRemovalResponse
    {
        public bool Success { get; init; }
        public string? Message { get; init; }
    }

    public async Task<TwoFactorSetupResponse?> InitiateTwoFactorSetupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _api.PostAsync<TwoFactorSetupResponse>(
                "api/2fa/setup",
                new { },
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TwoFactorEnableResponse?> VerifyAndEnableTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _api.PostAsync<TwoFactorEnableResponse>(
                "api/2fa/enable",
                new TwoFactorEnableRequest { Code = code },
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TwoFactorRecoveryCodesResponse?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _api.PostAsync<TwoFactorRecoveryCodesResponse>(
                "api/2fa/recovery-codes/generate",
                new { },
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TwoFactorDisableResponse?> DisableTwoFactorAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _api.PostAsync<TwoFactorDisableResponse>(
                "api/2fa/disable",
                new { },
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _api.PostPublicRawAsync(
                "api/account/forgot-password",
                new { email },
                cancellationToken);
            // We ignore failures to avoid enumerating users.
        }
        catch
        {
            // Ignore connection errors for forgot password
        }
    }

    public async Task<PasswordResetResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        using var response = await _api.PostPublicRawAsync(
            "api/account/reset-password",
            new
            {
                email,
                token,
                newPassword
            },
            cancellationToken);

        ApiErrorResponse? error = null;
        try
        {
            error = await ApiHttpClientService.DeserializeAsync<ApiErrorResponse>(response, cancellationToken);
        }
        catch
        {
            // Fall back to status-code handling when the server body is not the standard error payload.
        }

        if (response.IsSuccessStatusCode)
        {
            return PasswordResetResult.Ok(error?.Message ?? "Password has been reset successfully.");
        }

        return PasswordResetResult.Fail(
            error?.Message ?? "Failed to reset password.",
            error?.Errors);
    }

    public async Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _api.GetAsync<TwoFactorStatusResponse>("api/2fa/status", cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _api.PostRawAsync(
                "api/logout",
                new { refreshToken = _tokenStorage.RefreshToken },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    $"[Logout] Backend logout returned {(int)response.StatusCode} ({response.StatusCode}); " +
                    "clearing the local session anyway.");
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                $"[Logout] Backend logout was canceled ({ex.Message}); clearing the local session anyway.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "[Logout] Backend logout failed; clearing the local session anyway.",
                ex);
        }
        finally
        {
            _tokenStorage.Clear();
        }
    }

    private static async Task<AuthResult> HandleErrorResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ApiErrorResponse? error = null;
        try
        {
            error = await ApiHttpClientService.DeserializeAsync<ApiErrorResponse>(response, ct);
        }
        catch
        {
            // If we can't parse the error body, fall through to status-code-based handling.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Forbidden when error?.ErrorCode == "EMAIL_NOT_VERIFIED" =>
                AuthResult.EmailVerificationRequired(error.Email),

            HttpStatusCode.Forbidden when error?.ErrorCode is "APP_VERSION_BLOCKED" or "APP_VERSION_REQUIRED" =>
                AuthResult.Fail(error.Message ?? "This app version is not allowed to access the API."),

            HttpStatusCode.Conflict when error?.ErrorCode == "DEVICE_LIMIT_EXCEEDED" =>
                AuthResult.DeviceLimit(error.Message, error.Devices),

            HttpStatusCode.Unauthorized =>
                AuthResult.Fail(error?.Message ?? "Invalid email or password."),

            HttpStatusCode.BadRequest =>
                AuthResult.Fail(error?.Errors is { Count: > 0 }
                    ? string.Join(" ", error.Errors)
                    : error?.Message ?? "Invalid request."),

            _ => AuthResult.Fail(error?.Message ?? $"Server error ({(int)response.StatusCode}).")
        };
    }

    public async Task<AuthResult> LoginWithOAuthAsync(string email, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("[OAuthLogin] Legacy email-only OAuth completion requested after backend deprecation.");
        await Task.CompletedTask;
        return AuthResult.Fail("Legacy OAuth completion is disabled. Use Google sign-in instead.");
    }

    public async Task<AuthResult> LoginWithGoogleAsync(GoogleLoginContext loginContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loginContext);

        if (string.IsNullOrWhiteSpace(loginContext.ClientId) ||
            string.IsNullOrWhiteSpace(loginContext.AuthorizationCode) ||
            string.IsNullOrWhiteSpace(loginContext.RedirectUri) ||
            string.IsNullOrWhiteSpace(loginContext.CodeVerifier))
        {
            return AuthResult.Fail(loginContext.ErrorMessage ?? "Google sign-in did not return an authorization code.");
        }

        _logger.LogInformation("[GoogleLogin] Initiating Google login with an authorization code.");
        var deviceKey = _deviceKeyService.GetRegistration();

        var body = new
        {
            clientId = loginContext.ClientId,
            code = loginContext.AuthorizationCode,
            redirectUri = loginContext.RedirectUri,
            codeVerifier = loginContext.CodeVerifier,
            deviceId = _tokenStorage.DeviceId,
            appVersion = AppVersionProvider.GetApiVersion(),
            devicePublicKey = deviceKey.DevicePublicKey,
            devicePublicKeyId = deviceKey.DevicePublicKeyId,
            devicePublicKeyAlgorithm = deviceKey.DevicePublicKeyAlgorithm
        };

        using var response = await _api.PostPublicRawAsync("api/login/google/code", body, cancellationToken);
        _logger.LogInformation($"[GoogleLogin] Response Status: {response.StatusCode}");

        if (response.IsSuccessStatusCode)
        {
            var login = await ApiHttpClientService.DeserializeAsync<LoginResponse>(response, cancellationToken);
            if (login is null)
                return AuthResult.Fail("Unexpected server response.");

            if (login.RequiresTwoFactor)
                return AuthResult.TwoFactorRequired(login.Email ?? string.Empty, login.UserId ?? string.Empty, login.PendingLoginToken);

            if (login.Token is not null && login.RefreshToken is not null)
            {
                _logger.LogInformation($"[GoogleLogin] Login successful. User: {login.Email}");
                _tokenStorage.StoreSession(
                    login.Token,
                    login.RefreshToken,
                    login.UserId ?? string.Empty,
                    login.Email ?? string.Empty, 
                    login.PlanType); // PlanType might be null from Google endpoint, handled in Plan getter

                return AuthResult.Ok();
            }

            _logger.LogWarning($"[GoogleLogin] Login success but missing tokens. Message: {login?.Message}");
            return AuthResult.Fail(login?.Message ?? "Google login failed.");
        }

        var errorResult = await HandleErrorResponseAsync(response, cancellationToken);
        _logger.LogError($"[GoogleLogin] Login failed. Error: {errorResult.ErrorMessage}");
        return errorResult;
    }
}
