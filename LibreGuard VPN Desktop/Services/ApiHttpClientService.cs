using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Centralized HTTP client for the LibreGuard management API.
/// Handles Bearer auth attachment and automatic token refresh on 401.
/// </summary>
internal sealed class ApiHttpClientService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly TokenStorageService _tokenStorage;
    private readonly DeviceKeyService _deviceKeyService;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Task<bool>? _refreshTask;
    private readonly object _refreshTaskLock = new();
    private CancellationTokenSource? _backgroundRefreshCts;

    public ApiHttpClientService(TokenStorageService tokenStorage, DeviceKeyService deviceKeyService)
        : this(tokenStorage, deviceKeyService, new HttpClient
        {
            BaseAddress = new Uri("https://management.libreguard.net/"),
            Timeout = TimeSpan.FromSeconds(30)
        })
    {
    }

    internal ApiHttpClientService(
        TokenStorageService tokenStorage,
        DeviceKeyService deviceKeyService,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(tokenStorage);
        ArgumentNullException.ThrowIfNull(deviceKeyService);
        ArgumentNullException.ThrowIfNull(httpClient);

        _tokenStorage = tokenStorage;
        _deviceKeyService = deviceKeyService;
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri("https://management.libreguard.net/");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _tokenStorage.SessionChanged += OnSessionChanged;
        StartBackgroundRefresh();
    }

    private void OnSessionChanged()
    {
        StartBackgroundRefresh();
    }

    private void StartBackgroundRefresh()
    {
        _backgroundRefreshCts?.Cancel();
        _backgroundRefreshCts?.Dispose();
        _backgroundRefreshCts = new CancellationTokenSource();

        var token = _tokenStorage.AccessToken;
        if (string.IsNullOrEmpty(token)) return;

        var exp = GetTokenExpiration(token);
        if (exp == null) return;

        var timeToRefresh = exp.Value - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
        if (timeToRefresh <= TimeSpan.Zero)
        {
            // Token is already expired or about to expire, refresh immediately
            _ = Task.Run(() => TryRefreshTokenAsync(_backgroundRefreshCts.Token));
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(timeToRefresh, _backgroundRefreshCts.Token);
                await TryRefreshTokenAsync(_backgroundRefreshCts.Token);
            }
            catch (OperationCanceledException) { /* Expected */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] Background refresh failed: {ex.Message}");
            }
        });
    }

    private static DateTimeOffset? GetTokenExpiration(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var bytes = Convert.FromBase64String(payload);
            var json = Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var expElement) && expElement.TryGetInt64(out var exp))
            {
                return DateTimeOffset.FromUnixTimeSeconds(exp);
            }
        }
        catch { /* Ignore parsing errors */ }
        return null;
    }

    /// <summary>
    /// Sends a GET request with automatic Bearer auth. Retries once on 401 after token refresh.
    /// </summary>
    public Task<T?> GetAsync<T>(string requestUri, CancellationToken ct = default) =>
        SendWithAuthAsync<T>(HttpMethod.Get, requestUri, content: null, ct);

    /// <summary>
    /// Sends a POST request with JSON body and automatic Bearer auth.
    /// </summary>
    public Task<T?> PostAsync<T>(string requestUri, object? body = null, CancellationToken ct = default) =>
        SendWithAuthAsync<T>(HttpMethod.Post, requestUri, body, ct);

    /// <summary>
    /// Sends a POST request without expecting a typed response body.
    /// Returns the raw HttpResponseMessage so callers can inspect status codes.
    /// </summary>
    public Task<HttpResponseMessage> PostRawAsync(string requestUri, object? body = null, CancellationToken ct = default) =>
        SendRawWithAuthAsync(HttpMethod.Post, requestUri, body, ct);

    /// <summary>
    /// Sends a PUT request with JSON body and automatic Bearer auth.
    /// Returns the raw HttpResponseMessage so callers can inspect status codes and error payloads.
    /// </summary>
    public Task<HttpResponseMessage> PutRawAsync(string requestUri, object? body = null, CancellationToken ct = default) =>
        SendRawWithAuthAsync(HttpMethod.Put, requestUri, body, ct);

    /// <summary>
    /// Sends a GET request without Bearer auth (for public/unauthenticated endpoints).
    /// </summary>
    public async Task<T?> GetPublicAsync<T>(string requestUri, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await DeserializeAsync<T>(response, ct);
    }

    /// <summary>
    /// Sends a POST request without Bearer auth (e.g., login, register).
    /// Returns the raw HttpResponseMessage for status inspection.
    /// </summary>
    public async Task<HttpResponseMessage> PostPublicRawAsync(string requestUri, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        return await _httpClient.SendAsync(request, ct);
    }

    /// <summary>
    /// Attempts to refresh the access token using the stored refresh token.
    /// Returns true if the new tokens were stored successfully.
    /// </summary>
    public Task<bool> TryRefreshTokenAsync(CancellationToken ct = default, bool force = false)
    {
        lock (_refreshTaskLock)
        {
            if (!force && _refreshTask != null && !_refreshTask.IsCompleted)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: Attaching to ongoing refresh task...");
                return _refreshTask;
            }

            _refreshTask = TryRefreshTokenInternalAsync(ct, force);
            return _refreshTask;
        }
    }

    private async Task<bool> TryRefreshTokenInternalAsync(CancellationToken ct, bool force)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // PROACTIVE: Check if another thread already refreshed while we were waiting at the lock
            var currentToken = _tokenStorage.AccessToken;
            if (!force && !string.IsNullOrEmpty(currentToken))
            {
                var exp = GetTokenExpiration(currentToken);
                if (exp != null && exp.Value - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
                {
                    System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: Token already fresh, skipping networking.");
                    return true;
                }
            }

            var refreshToken = _tokenStorage.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: No refresh token stored");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: Starting token refresh HTTP call...");
            var deviceKey = _deviceKeyService.GetRegistration();

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/login/refresh");
            request.Content = JsonContent.Create(new
            {
                refreshToken,
                deviceId = _tokenStorage.DeviceId,
                appVersion = AppVersionProvider.GetApiVersion(),
                devicePublicKey = deviceKey.DevicePublicKey,
                devicePublicKeyId = deviceKey.DevicePublicKeyId,
                devicePublicKeyAlgorithm = deviceKey.DevicePublicKeyAlgorithm
            }, options: JsonOptions);

            // Attach current (possibly expired) token - the server validates refresh token, not access token
            if (_tokenStorage.AccessToken is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, ct);
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: Refresh response: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: Refresh failed with {response.StatusCode}");
                await InvalidateSessionIfAuthRejectedAsync(response, ct);
                return false;
            }

            var result = await DeserializeAsync<LoginResponse>(response, ct);
            if (result?.Token is null || result.RefreshToken is null)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: Refresh response missing Token or RefreshToken");
                return false;
            }

            _tokenStorage.StoreSession(
                result.Token,
                result.RefreshToken,
                result.UserId ?? _tokenStorage.UserId ?? string.Empty,
                result.Email ?? _tokenStorage.Email ?? string.Empty,
                result.PlanType ?? _tokenStorage.PlanType);

            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: Tokens refreshed successfully");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine("[ApiHttpClientService] TryRefreshTokenAsync: Cancelled by caller");
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] TryRefreshTokenAsync: Exception during refresh: {ex.Message}");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Deserializes JSON response content.
    /// </summary>
    public static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    public void Dispose()
    {
        _tokenStorage.SessionChanged -= OnSessionChanged;
        _backgroundRefreshCts?.Cancel();
        _backgroundRefreshCts?.Dispose();
        _httpClient.Dispose();
        _refreshLock.Dispose();
    }

    internal void InvalidateSession()
    {
        _tokenStorage.Clear();
    }

    private async Task<T?> SendWithAuthAsync<T>(HttpMethod method, string requestUri, object? content, CancellationToken ct)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendWithAuthAsync<{typeof(T).Name}> START: {method} {requestUri}");

            using var response = await SendRawWithAuthAsync(method, requestUri, content, ct);

            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendWithAuthAsync<{typeof(T).Name}> Response: {response.StatusCode} - {response.ReasonPhrase}");

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendWithAuthAsync<{typeof(T).Name}> ERROR: Status {response.StatusCode}");

                string? errorBody = null;
                // Log response body for error diagnostics
                try
                {
                    errorBody = await response.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrWhiteSpace(errorBody))
                        System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] Response Body: {errorBody.Substring(0, Math.Min(500, errorBody.Length))}");
                }
                catch (Exception bodyEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] Could not read error response body: {bodyEx.Message}");
                }

                if (IsForcedLogoutResponse(response.StatusCode, errorBody))
                {
                    System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] Authentication/session invalidated by API - clearing tokens");
                    _tokenStorage.Clear();
                }
                return default;
            }

            var result = await DeserializeAsync<T>(response, ct);
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendWithAuthAsync<{typeof(T).Name}> SUCCESS - deserialized result");
            return result;
        }
        catch (OperationCanceledException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendWithAuthAsync<{typeof(T).Name}> CANCELLED: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendWithAuthAsync<{typeof(T).Name}> EXCEPTION: {ex.GetType().Name} - {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] Stack Trace: {ex.StackTrace}");
            throw;
        }
    }

    private static bool IsForcedLogoutResponse(HttpStatusCode statusCode, string? errorBody)
    {
        if (statusCode != HttpStatusCode.Forbidden || string.IsNullOrWhiteSpace(errorBody))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<ApiErrorResponse>(errorBody, JsonOptions);
            if (parsed?.RequiresDeviceRegistration == true)
                return true;

            if (string.Equals(parsed?.ErrorCode, "DEVICE_NOT_REGISTERED", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // Fallback to string matching when payload shape differs.
        }

        return errorBody.Contains("\"errorCode\":\"DEVICE_NOT_REGISTERED\"", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("Please login again", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendRawWithAuthAsync(HttpMethod method, string requestUri, object? content, CancellationToken ct)
    {
        var token = _tokenStorage.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            var exp = GetTokenExpiration(token);
            if (exp != null && exp.Value - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(1))
            {
                try
                {
                    await TryRefreshTokenAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] Proactive refresh failed: {ex.Message}");
                }
            }
        }

        var response = await SendOnceAsync(method, requestUri, content, ct);

        // A 401 can be caused by server-side invalidation even when the JWT's exp claim
        // is still in the future. Force the refresh so it cannot be skipped by the
        // proactive freshness check.
        if (response.StatusCode == HttpStatusCode.Unauthorized && await TryRefreshTokenAsync(ct, force: true))
        {
            response.Dispose();
            response = await SendOnceAsync(method, requestUri, content, ct);

            // A newly-issued token being rejected immediately is an authoritative
            // authentication failure rather than a transient refresh outage.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                _tokenStorage.Clear();
        }

        await InvalidateSessionIfForcedLogoutResponseAsync(response, ct);

        return response;
    }

    private async Task InvalidateSessionIfForcedLogoutResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return;

        string? errorBody = null;
        try
        {
            errorBody = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return;
        }

        if (IsForcedLogoutResponse(response.StatusCode, errorBody))
            _tokenStorage.Clear();
    }

    private async Task InvalidateSessionIfAuthRejectedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.BadRequest)
        {
            _tokenStorage.Clear();
            return;
        }

        await InvalidateSessionIfForcedLogoutResponseAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string requestUri, object? content, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, requestUri);

            if (content is not null)
                request.Content = JsonContent.Create(content, options: JsonOptions);

            var accessToken = _tokenStorage.AccessToken;
            var hasToken = accessToken is not null;
            if (hasToken)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendOnceAsync: {method} {_httpClient.BaseAddress}{requestUri}, Token: {(hasToken ? "YES" : "NO")}");

            // We cannot dispose the response here - callers own it
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendOnceAsync Response Code: {response.StatusCode}");
            return response;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendOnceAsync HTTP ERROR: {ex.GetType().Name} - {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] Inner Exception: {ex.InnerException?.Message}");
            throw;
        }
        catch (OperationCanceledException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiHttpClientService] SendOnceAsync TIMEOUT/CANCEL: {ex.Message}");
            throw;
        }
    }
}
