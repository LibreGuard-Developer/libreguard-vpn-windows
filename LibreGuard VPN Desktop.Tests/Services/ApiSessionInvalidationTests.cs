using System.Net;
using System.Net.Http;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class ApiSessionInvalidationTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task LogoutAsync_WhenBackendRejectsSession_ClearsLocalSession(HttpStatusCode statusCode)
    {
        var tokenStorage = CreateTokenStorage();
        tokenStorage.StoreSession("access", "refresh", "user-1", "user@example.test", "Pro");
        using var api = CreateApi(tokenStorage, _ => new HttpResponseMessage(statusCode));
        var auth = new ApiAuthenticationService(api, tokenStorage, new RecordingLoggerService(), CreateDeviceKeyService());

        await auth.LogoutAsync();

        Assert.False(tokenStorage.HasToken);
        Assert.False(auth.IsAuthenticated);
    }

    [Fact]
    public async Task LogoutAsync_WhenBackendReturnsServerError_ClearsLocalSession()
    {
        var tokenStorage = CreateTokenStorage();
        tokenStorage.StoreSession("access", "refresh", "user-1", "user@example.test", "Pro");
        using var api = CreateApi(tokenStorage, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var auth = new ApiAuthenticationService(api, tokenStorage, new RecordingLoggerService(), CreateDeviceKeyService());

        await auth.LogoutAsync();

        Assert.False(tokenStorage.HasToken);
        Assert.False(auth.IsAuthenticated);
    }

    [Fact]
    public async Task LogoutAsync_WhenBackendRequestFails_ClearsLocalSession()
    {
        var tokenStorage = CreateTokenStorage();
        tokenStorage.StoreSession("access", "refresh", "user-1", "user@example.test", "Pro");
        using var api = CreateApi(tokenStorage, _ => throw new HttpRequestException("Network unavailable."));
        var auth = new ApiAuthenticationService(api, tokenStorage, new RecordingLoggerService(), CreateDeviceKeyService());

        await auth.LogoutAsync();

        Assert.False(tokenStorage.HasToken);
        Assert.False(auth.IsAuthenticated);
    }

    [Fact]
    public async Task GetConfigAsync_WhenRefreshFailsTransiently_PreservesLocalSession()
    {
        var tokenStorage = CreateTokenStorage();
        tokenStorage.StoreSession("access", "refresh", "user-1", "user@example.test", "Pro");
        using var api = CreateApi(tokenStorage, request =>
            request.RequestUri?.AbsolutePath.EndsWith("/api/login/refresh", StringComparison.OrdinalIgnoreCase) == true
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{ "message": "unauthorized" }""")
                });
        var config = new ApiVpnConfigService(api);

        await Assert.ThrowsAsync<VpnConfigRequestException>(() => config.GetConfigAsync(1, "IKEV2"));

        Assert.True(tokenStorage.HasToken);
    }

    [Fact]
    public async Task GetConfigAsync_WhenBackendReturnsDeviceNotRegistered_ClearsLocalSession()
    {
        var tokenStorage = CreateTokenStorage();
        tokenStorage.StoreSession("access", "refresh", "user-1", "user@example.test", "Pro");
        using var api = CreateApi(tokenStorage, _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""
                {
                    "message": "Please login again.",
                    "errorCode": "DEVICE_NOT_REGISTERED"
                }
                """)
        });
        var config = new ApiVpnConfigService(api);

        await Assert.ThrowsAsync<VpnConfigRequestException>(() => config.GetConfigAsync(1, "IKEV2"));

        Assert.False(tokenStorage.HasToken);
    }

    private static ApiHttpClientService CreateApi(
        TokenStorageService tokenStorage,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory))
        {
            BaseAddress = new Uri("https://management.libreguard.test/")
        };

        return new ApiHttpClientService(tokenStorage, CreateDeviceKeyService(), httpClient);
    }

    private static TokenStorageService CreateTokenStorage()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "LibreGuardVPN.Tests",
            Guid.NewGuid().ToString("N"),
            "session.secure");

        return new TokenStorageService(path);
    }

    private static DeviceKeyService CreateDeviceKeyService()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "LibreGuardVPN.Tests",
            Guid.NewGuid().ToString("N"),
            "device_key.dpapi");

        return new DeviceKeyService(path);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class RecordingLoggerService : ILoggerService
    {
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }
}
