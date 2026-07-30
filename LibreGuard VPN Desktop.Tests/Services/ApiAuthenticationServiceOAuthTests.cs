using System.Net;
using System.Net.Http;
using System.Text.Json;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class ApiAuthenticationServiceOAuthTests
{
    [Fact]
    public async Task LoginWithGoogleAsync_PostsAuthorizationCodeToBackendCodeEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var tokenStorage = CreateTokenStorage();
        using var api = CreateApi(tokenStorage, async request =>
        {
            capturedRequest = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, """
                {
                    "requiresTwoFactor": false,
                    "token": "jwt",
                    "refreshToken": "refresh",
                    "email": "user@example.test",
                    "userId": "user-1",
                    "deviceId": "device-1",
                    "planType": "Pro"
                }
                """);
        });
        var auth = new ApiAuthenticationService(api, tokenStorage, new RecordingLoggerService(), CreateDeviceKeyService());

        var result = await auth.LoginWithGoogleAsync(CreateGoogleContext());

        Assert.True(result.Success);
        Assert.Equal("/api/login/google/code", capturedRequest?.RequestUri?.AbsolutePath);
        using var document = JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Assert.Equal("client.apps.googleusercontent.com", root.GetProperty("clientId").GetString());
        Assert.Equal("auth-code", root.GetProperty("code").GetString());
        Assert.Equal("http://localhost:54321/", root.GetProperty("redirectUri").GetString());
        Assert.Equal("code-verifier", root.GetProperty("codeVerifier").GetString());
        Assert.True(root.TryGetProperty("deviceId", out _));
        Assert.True(root.TryGetProperty("appVersion", out _));
        Assert.False(root.TryGetProperty("id" + "Token", out _));
        Assert.False(root.TryGetProperty("client" + "Secret", out _));
    }

    [Fact]
    public async Task RemoveDevicePreAuthOAuthAsync_PostsAuthorizationCodeToBackendRemoveCodeEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var tokenStorage = CreateTokenStorage();
        using var api = CreateApi(tokenStorage, async request =>
        {
            capturedRequest = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, """
                {
                    "success": true,
                    "message": "Device removed successfully.",
                    "deviceId": 7,
                    "removedDeviceCount": 1
                }
                """);
        });
        var auth = new ApiAuthenticationService(api, tokenStorage, new RecordingLoggerService(), CreateDeviceKeyService());

        var removed = await auth.RemoveDevicePreAuthOAuthAsync(CreateGoogleContext(), "Google", 7);

        Assert.True(removed.Success);
        Assert.Equal("Device removed successfully.", removed.Message);
        Assert.Equal("/api/devices/pre-auth/oauth/remove-code", capturedRequest?.RequestUri?.AbsolutePath);
        using var document = JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Assert.Equal("Google", root.GetProperty("provider").GetString());
        Assert.Equal("client.apps.googleusercontent.com", root.GetProperty("clientId").GetString());
        Assert.Equal("auth-code", root.GetProperty("code").GetString());
        Assert.Equal("http://localhost:54321/", root.GetProperty("redirectUri").GetString());
        Assert.Equal("code-verifier", root.GetProperty("codeVerifier").GetString());
        Assert.Equal(7, root.GetProperty("deviceIdToRemove").GetInt32());
        Assert.False(root.TryGetProperty("id" + "Token", out _));
        Assert.False(root.TryGetProperty("client" + "Secret", out _));
    }

    [Fact]
    public async Task RemoveDevicePreAuthAsync_PostsPasswordRequestToBackendEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var tokenStorage = CreateTokenStorage();
        using var api = CreateApi(tokenStorage, async request =>
        {
            capturedRequest = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, """
                {
                    "success": true,
                    "message": "Device removed successfully."
                }
                """);
        });
        var auth = new ApiAuthenticationService(api, tokenStorage, new RecordingLoggerService(), CreateDeviceKeyService());

        var removed = await auth.RemoveDevicePreAuthAsync("user@example.test", "password", 9);

        Assert.True(removed.Success);
        Assert.Equal("/api/devices/pre-auth/remove", capturedRequest?.RequestUri?.AbsolutePath);
        using var document = JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Assert.Equal("user@example.test", root.GetProperty("email").GetString());
        Assert.Equal("password", root.GetProperty("password").GetString());
        Assert.Equal(9, root.GetProperty("deviceIdToRemove").GetInt32());
    }

    [Fact]
    public async Task RemoveDevicePreAuthOAuthAsync_MapsBackendErrorDetails()
    {
        var tokenStorage = CreateTokenStorage();
        using var api = CreateApi(tokenStorage, _ => Task.FromResult(JsonResponse(HttpStatusCode.TooManyRequests, """
            {
                "message": "Too many requests.",
                "errorCode": "RATE_LIMIT_EXCEEDED",
                "retryAfterSeconds": 45
            }
            """)));
        var auth = new ApiAuthenticationService(api, tokenStorage, new RecordingLoggerService(), CreateDeviceKeyService());

        var removed = await auth.RemoveDevicePreAuthOAuthAsync(CreateGoogleContext(), "Google", 7);

        Assert.False(removed.Success);
        Assert.Equal("Too many requests.", removed.Message);
        Assert.Equal("RATE_LIMIT_EXCEEDED", removed.ErrorCode);
        Assert.Equal(45, removed.RetryAfterSeconds);
    }

    [Fact]
    public async Task LoginWithGoogleAsync_WhenAuthorizationCodeMissing_DoesNotCallBackend()
    {
        var tokenStorage = CreateTokenStorage();
        using var api = CreateApi(tokenStorage, _ => throw new InvalidOperationException("Backend should not be called."));
        var auth = new ApiAuthenticationService(api, tokenStorage, new RecordingLoggerService(), CreateDeviceKeyService());

        var result = await auth.LoginWithGoogleAsync(new GoogleLoginContext { ClientId = "client.apps.googleusercontent.com" });

        Assert.False(result.Success);
        Assert.Contains("authorization code", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoogleLoginContext_HasCompletionData_RequiresCodePkceFields()
    {
        Assert.True(CreateGoogleContext().HasCompletionData);
        Assert.False((CreateGoogleContext() with { AuthorizationCode = null }).HasCompletionData);
        Assert.False((CreateGoogleContext() with { CodeVerifier = null }).HasCompletionData);
        Assert.False((CreateGoogleContext() with { RedirectUri = null }).HasCompletionData);
        Assert.False((CreateGoogleContext() with { ClientId = null }).HasCompletionData);
    }

    private static GoogleLoginContext CreateGoogleContext() => new()
    {
        ClientId = "client.apps.googleusercontent.com",
        AuthorizationCode = "auth-code",
        RedirectUri = "http://localhost:54321/",
        CodeVerifier = "code-verifier"
    };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json)
    };

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
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _responseFactory(request);
        }
    }

    private sealed class RecordingLoggerService : ILoggerService
    {
        public void LogInformation(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }
}
