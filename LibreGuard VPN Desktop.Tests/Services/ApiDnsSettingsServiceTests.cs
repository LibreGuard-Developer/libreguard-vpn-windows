using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class ApiDnsSettingsServiceTests
{
    [Fact]
    public async Task GetPreferenceAsync_UsesAuthenticatedSettingsEndpointAndDeserializesResponse()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "refresh-token", "user-1", "user@example.test", "Pro");

        HttpMethod? method = null;
        string? path = null;
        string? bearerToken = null;
        using var api = CreateApi(storage, request =>
        {
            method = request.Method;
            path = request.RequestUri?.AbsolutePath;
            bearerToken = request.Headers.Authorization?.Parameter;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, PreferenceJson(requestedEnabled: true)));
        });

        var service = new ApiDnsSettingsService(api);
        var preference = await service.GetPreferenceAsync();

        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("/api/dns/settings", path);
        Assert.Equal(storage.AccessToken, bearerToken);
        Assert.NotNull(preference);
        Assert.True(preference.RequestedEnabled);
        Assert.True(preference.CanUseAdBlocking);
        Assert.True(preference.EffectiveEnabled);
        Assert.Equal("filtered", preference.EffectiveMode);
        Assert.Equal(15, preference.PropagationSeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetAdBlockingAsync_SendsExpectedJsonAndReturnsUpdatedPreference(bool enabled)
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "refresh-token", "user-1", "user@example.test", "Pro");

        HttpMethod? method = null;
        string? path = null;
        string? requestBody = null;
        using var api = CreateApi(storage, async request =>
        {
            method = request.Method;
            path = request.RequestUri?.AbsolutePath;
            requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, PreferenceJson(requestedEnabled: enabled));
        });

        var service = new ApiDnsSettingsService(api);
        var result = await service.SetAdBlockingAsync(enabled);

        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/api/dns/settings", path);
        using var json = JsonDocument.Parse(requestBody!);
        Assert.Equal(enabled, json.RootElement.GetProperty("adBlockingEnabled").GetBoolean());
        Assert.Single(json.RootElement.EnumerateObject());

        Assert.True(result.Success);
        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Preference);
        Assert.Equal(enabled, result.Preference.RequestedEnabled);
        Assert.Equal(enabled, result.Preference.EffectiveEnabled);
    }

    [Fact]
    public async Task SetAdBlockingAsync_WhenProIsRequired_ReturnsNestedAuthoritativeSettings()
    {
        var storage = CreateTokenStorage();
        var accessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(1));
        storage.StoreSession(accessToken, "refresh-token", "user-1", "user@example.test", "Free");

        using var api = CreateApi(storage, _ => Task.FromResult(JsonResponse(HttpStatusCode.Forbidden, """
            {
                "errorCode": "PRO_REQUIRED",
                "message": "Ad blocking requires an active Pro subscription.",
                "settings": {
                    "requestedEnabled": false,
                    "canUseAdBlocking": false,
                    "effectiveEnabled": false,
                    "effectiveMode": "regular",
                    "propagationSeconds": 15
                }
            }
            """)));

        var service = new ApiDnsSettingsService(api);
        var result = await service.SetAdBlockingAsync(enabled: true);

        Assert.False(result.Success);
        Assert.Equal("PRO_REQUIRED", result.ErrorCode);
        Assert.Equal("Ad blocking requires an active Pro subscription.", result.Message);
        Assert.NotNull(result.Preference);
        Assert.False(result.Preference.RequestedEnabled);
        Assert.False(result.Preference.CanUseAdBlocking);
        Assert.Equal("regular", result.Preference.EffectiveMode);
        Assert.Equal(accessToken, storage.AccessToken);
    }

    [Fact]
    public async Task SetAdBlockingAsync_WhenPutReturns401_RefreshesAndRetriesWithRotatedToken()
    {
        var storage = CreateTokenStorage();
        var oldAccessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(1));
        storage.StoreSession(oldAccessToken, "old-refresh", "user-1", "user@example.test", "Pro");

        var settingsRequestCount = 0;
        var settingsBearerTokens = new List<string?>();
        using var api = CreateApi(storage, request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/login/refresh")
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """
                    {
                        "token": "new-access",
                        "refreshToken": "new-refresh",
                        "userId": "user-1",
                        "email": "user@example.test",
                        "planType": "Pro"
                    }
                    """));
            }

            settingsRequestCount++;
            settingsBearerTokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(settingsRequestCount == 1
                ? JsonResponse(HttpStatusCode.Unauthorized, "{}")
                : JsonResponse(HttpStatusCode.OK, PreferenceJson(requestedEnabled: true)));
        });

        var service = new ApiDnsSettingsService(api);
        var result = await service.SetAdBlockingAsync(enabled: true);

        Assert.True(result.Success);
        Assert.Equal(2, settingsRequestCount);
        Assert.Equal(new[] { oldAccessToken, "new-access" }, settingsBearerTokens);
        Assert.Equal("new-access", storage.AccessToken);
        Assert.Equal("new-refresh", storage.RefreshToken);
    }

    [Fact]
    public async Task SetAdBlockingAsync_WhenNetworkFails_ReturnsRetryableFailure()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "refresh-token", "user-1", "user@example.test", "Pro");

        using var api = CreateApi(storage, _ => throw new HttpRequestException("Temporary network failure."));
        var service = new ApiDnsSettingsService(api);

        var result = await service.SetAdBlockingAsync(enabled: true);

        Assert.False(result.Success);
        Assert.Equal("NETWORK_ERROR", result.ErrorCode);
        Assert.Null(result.Preference);
    }

    [Fact]
    public async Task SetAdBlockingAsync_WhenCallerCancels_PropagatesCancellation()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "refresh-token", "user-1", "user@example.test", "Pro");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var api = CreateApi(storage, _ => Task.FromCanceled<HttpResponseMessage>(cts.Token));
        var service = new ApiDnsSettingsService(api);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SetAdBlockingAsync(enabled: true, cts.Token));
    }

    [Fact]
    public async Task SetAdBlockingAsync_WhenHttpClientTimesOut_ReturnsTimeoutFailure()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "refresh-token", "user-1", "user@example.test", "Pro");

        using var api = CreateApi(storage, _ =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated HttpClient timeout.")));
        var service = new ApiDnsSettingsService(api);

        var result = await service.SetAdBlockingAsync(enabled: true);

        Assert.False(result.Success);
        Assert.Equal("TIMEOUT", result.ErrorCode);
        Assert.Null(result.Preference);
    }

    [Fact]
    public async Task SetAdBlockingAsync_WhenServerReturns5xx_ReturnsRetryableServerFailure()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "refresh-token", "user-1", "user@example.test", "Pro");

        using var api = CreateApi(storage, _ => Task.FromResult(JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            "{\"message\":\"DNS policy is temporarily unavailable.\"}")));
        var service = new ApiDnsSettingsService(api);

        var result = await service.SetAdBlockingAsync(enabled: true);

        Assert.False(result.Success);
        Assert.Equal("SERVER_ERROR", result.ErrorCode);
        Assert.Equal("DNS policy is temporarily unavailable.", result.Message);
        Assert.Null(result.Preference);
    }

    [Fact]
    public async Task SetAdBlockingAsync_WhenDeviceRegistrationIsRequired_ClearsSession()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "refresh-token", "user-1", "user@example.test", "Pro");

        using var api = CreateApi(storage, _ => Task.FromResult(JsonResponse(HttpStatusCode.Forbidden, """
            {
                "errorCode": "DEVICE_NOT_REGISTERED",
                "message": "Please login again.",
                "requiresDeviceRegistration": true
            }
            """)));
        var service = new ApiDnsSettingsService(api);

        var result = await service.SetAdBlockingAsync(enabled: true);

        Assert.False(result.Success);
        Assert.Equal("DEVICE_NOT_REGISTERED", result.ErrorCode);
        Assert.Null(storage.AccessToken);
        Assert.Null(storage.RefreshToken);
    }

    private static ApiHttpClientService CreateApi(
        TokenStorageService tokenStorage,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory))
        {
            BaseAddress = new Uri("https://management.libreguard.test/")
        };

        return new ApiHttpClientService(tokenStorage, CreateDeviceKeyService(), httpClient);
    }

    private static TokenStorageService CreateTokenStorage()
    {
        var path = Path.Combine(Path.GetTempPath(), "LibreGuardVPN.Tests", Guid.NewGuid().ToString("N"), "session.secure");
        return new TokenStorageService(path);
    }

    private static DeviceKeyService CreateDeviceKeyService()
    {
        var path = Path.Combine(Path.GetTempPath(), "LibreGuardVPN.Tests", Guid.NewGuid().ToString("N"), "device_key.dpapi");
        return new DeviceKeyService(path);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string PreferenceJson(bool requestedEnabled) => $$"""
        {
            "requestedEnabled": {{requestedEnabled.ToString().ToLowerInvariant()}},
            "canUseAdBlocking": true,
            "effectiveEnabled": {{requestedEnabled.ToString().ToLowerInvariant()}},
            "effectiveMode": "filtered",
            "propagationSeconds": 15
        }
        """;

    private static string CreateJwt(DateTimeOffset expiresAt)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expiresAt.ToUnixTimeSeconds()}}}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"header.{payload}.signature";
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _responseFactory(request);
    }
}
