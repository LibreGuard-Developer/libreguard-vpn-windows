using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class ApiHttpClientServiceRefreshTests
{
    [Fact]
    public async Task GetAsync_WhenServerReturns401WithFreshJwt_RefreshesAndRetriesWithRotatedTokens()
    {
        var storage = CreateTokenStorage();
        var oldAccessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(1));
        storage.StoreSession(oldAccessToken, "old-refresh", "user-1", "user@example.test", "Free");

        string? refreshRequestBody = null;
        string? retriedAccessToken = null;
        var protectedRequestCount = 0;

        using var api = CreateApi(storage, async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/login/refresh")
            {
                refreshRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
                return JsonResponse(HttpStatusCode.OK, """
                    {
                        "token": "new-access",
                        "refreshToken": "new-refresh",
                        "userId": "user-1",
                        "email": "user@example.test"
                    }
                    """);
            }

            protectedRequestCount++;
            retriedAccessToken = request.Headers.Authorization?.Parameter;
            return protectedRequestCount == 1
                ? JsonResponse(HttpStatusCode.Unauthorized, "{}")
                : JsonResponse(HttpStatusCode.OK, """{"value":"retried"}""");
        });

        var result = await api.GetAsync<TestResponse>("api/protected");

        Assert.Equal("retried", result?.Value);
        Assert.Equal(2, protectedRequestCount);
        Assert.Equal("new-access", retriedAccessToken);
        Assert.Equal("new-access", storage.AccessToken);
        Assert.Equal("new-refresh", storage.RefreshToken);

        using var refreshBody = JsonDocument.Parse(refreshRequestBody!);
        Assert.Equal("old-refresh", refreshBody.RootElement.GetProperty("refreshToken").GetString());
        Assert.Equal(storage.DeviceId, refreshBody.RootElement.GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task GetAsync_WhenRetriedRequestStillReturns401_ClearsSession()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "old-refresh", "user-1", "user@example.test", "Free");

        var protectedRequestCount = 0;
        using var api = CreateApi(storage, request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/login/refresh")
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """
                    {
                        "token": "new-access",
                        "refreshToken": "new-refresh",
                        "userId": "user-1",
                        "email": "user@example.test"
                    }
                    """));
            }

            protectedRequestCount++;
            return Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, "{}"));
        });

        var result = await api.GetAsync<TestResponse>("api/protected");

        Assert.Null(result);
        Assert.Equal(2, protectedRequestCount);
        Assert.Null(storage.AccessToken);
        Assert.Null(storage.RefreshToken);
    }

    [Fact]
    public async Task GetAsync_WhenCallerCancelsDuringProactiveRefresh_PropagatesCancellation()
    {
        var storage = CreateTokenStorage();
        var accessToken = CreateJwt(DateTimeOffset.UtcNow.AddSeconds(30));
        storage.StoreSession(accessToken, "old-refresh", "user-1", "user@example.test", "Free");

        using var cts = new CancellationTokenSource();
        using var api = CreateApi(storage, request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/login/refresh")
            {
                cts.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cts.Token);
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"value\":\"unexpected\"}"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.GetAsync<TestResponse>("api/protected", cts.Token));

        Assert.Equal(accessToken, storage.AccessToken);
        Assert.Equal("old-refresh", storage.RefreshToken);
    }

    [Fact]
    public async Task TryRefreshTokenAsync_WhenCallerCancelsRefresh_PropagatesCancellation()
    {
        var storage = CreateTokenStorage();
        var accessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(1));
        storage.StoreSession(accessToken, "old-refresh", "user-1", "user@example.test", "Free");

        using var cts = new CancellationTokenSource();
        using var api = CreateApi(storage, request =>
        {
            Assert.Equal("/api/login/refresh", request.RequestUri?.AbsolutePath);
            cts.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.TryRefreshTokenAsync(cts.Token, force: true));

        Assert.Equal(accessToken, storage.AccessToken);
        Assert.Equal("old-refresh", storage.RefreshToken);
    }

    [Fact]
    public async Task GetAsync_WhenRefreshIsRejected_ClearsSession()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "old-refresh", "user-1", "user@example.test", "Free");

        using var api = CreateApi(storage, request => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/login/refresh"
                ? JsonResponse(HttpStatusCode.Unauthorized, """{"message":"Invalid refresh token."}""")
                : JsonResponse(HttpStatusCode.Unauthorized, "{}")));

        var result = await api.GetAsync<TestResponse>("api/protected");

        Assert.Null(result);
        Assert.Null(storage.AccessToken);
        Assert.Null(storage.RefreshToken);
    }

    [Fact]
    public async Task GetAsync_WhenRefreshFailsTransiently_PreservesSession()
    {
        var storage = CreateTokenStorage();
        var accessToken = CreateJwt(DateTimeOffset.UtcNow.AddHours(1));
        storage.StoreSession(accessToken, "old-refresh", "user-1", "user@example.test", "Free");

        using var api = CreateApi(storage, request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/login/refresh")
                throw new HttpRequestException("Temporary network failure.");

            return Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, "{}"));
        });

        var result = await api.GetAsync<TestResponse>("api/protected");

        Assert.Null(result);
        Assert.Equal(accessToken, storage.AccessToken);
        Assert.Equal("old-refresh", storage.RefreshToken);
    }

    [Fact]
    public async Task GetAsync_WhenServerRequiresDeviceRegistration_ClearsSession()
    {
        var storage = CreateTokenStorage();
        storage.StoreSession(CreateJwt(DateTimeOffset.UtcNow.AddHours(1)), "old-refresh", "user-1", "user@example.test", "Free");

        using var api = CreateApi(storage, _ => Task.FromResult(JsonResponse(HttpStatusCode.Forbidden, """
            {"errorCode":"DEVICE_NOT_REGISTERED","requiresDeviceRegistration":true}
            """)));

        var result = await api.GetAsync<TestResponse>("api/protected");

        Assert.Null(result);
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

    private static string CreateJwt(DateTimeOffset expiresAt)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expiresAt.ToUnixTimeSeconds()}}}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"header.{payload}.signature";
    }

    private sealed record TestResponse(string Value);

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
