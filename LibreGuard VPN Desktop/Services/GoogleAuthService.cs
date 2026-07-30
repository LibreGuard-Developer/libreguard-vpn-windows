using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

public class GoogleAuthService : IGoogleAuthService
{
    private sealed record GoogleClientConfiguration
    {
        public required string ClientId { get; init; }
    }

    private sealed record GoogleClientConfigurationFile
    {
        [JsonPropertyName("clientId")]
        public string? ClientId { get; init; }

        [JsonPropertyName("installed")]
        public GoogleClientConfigurationSection? Installed { get; init; }

        [JsonPropertyName("web")]
        public GoogleClientConfigurationSection? Web { get; init; }
    }

    private sealed record GoogleClientConfigurationSection
    {
        [JsonPropertyName("client_id")]
        public string? ClientId { get; init; }
    }

    private readonly ILoggerService _logger;

    private const string GoogleAuthorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleScope = "openid email profile";
    private static readonly string[] GoogleConfigFileNames =
    [
        "google-oauth-client.json",
        "google_oauth_client.json"
    ];

    public GoogleAuthService(ILoggerService logger)
    {
        _logger = logger;
    }

    public async Task<GoogleLoginContext> LoginAsync(CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();
        var port = StartListenerOnFreePort(listener);
        var callbackUri = $"http://localhost:{port}/";
        var clientConfiguration = GetRequiredGoogleClientConfiguration();
        var codeVerifier = CreateCodeVerifier();
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var state = CreateState();
        var authUrl = BuildGoogleAuthorizationUrl(clientConfiguration.ClientId, callbackUri, codeChallenge, state);

        _logger.LogInformation($"[GoogleAuth] Starting browser OAuth code flow. Local callback: {callbackUri}");

        try
        {
            _logger.LogInformation("[GoogleAuth] Opening browser to Google OAuth URL.");
            Process.Start(new ProcessStartInfo(authUrl)
            {
                UseShellExecute = true
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            using var registration = cancellationToken.Register(() =>
            {
                try { listener.Abort(); } catch { /* ignore */ }
            });

            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("[GoogleAuth] OAuth login was cancelled by the user.");
                        throw;
                    }

                    _logger.LogWarning("[GoogleAuth] OAuth login timed out.");
                    return new GoogleLoginContext
                    {
                        ErrorMessage = "Google sign-in timed out. Please try again."
                    };
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                var result = await HandleCallbackAsync(context, state, timeoutCts.Token);
                if (result.Completed)
                {
                    return new GoogleLoginContext
                    {
                        ClientId = result.AuthorizationCode is null ? null : clientConfiguration.ClientId,
                        AuthorizationCode = result.AuthorizationCode,
                        RedirectUri = result.AuthorizationCode is null ? null : callbackUri,
                        CodeVerifier = result.AuthorizationCode is null ? null : codeVerifier,
                        ErrorMessage = result.ErrorMessage
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("[GoogleAuth] OAuth flow failed.", ex);
            throw;
        }
        finally
        {
            try { listener.Close(); } catch { /* best effort */ }
        }
    }

    private async Task<CallbackResult> HandleCallbackAsync(
        HttpListenerContext context,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var request = context.Request;

        if (!request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            const string invalidMethodMessage = "Google sign-in returned an unsupported callback.";
            _logger.LogWarning("[GoogleAuth] Unsupported OAuth callback method.");
            await SendResponseAsync(context, GetHtmlResponse("Login Failed", invalidMethodMessage, false), cancellationToken);
            return CallbackResult.CompletedFailure(invalidMethodMessage);
        }

        var queryError = FirstNonEmpty(request.QueryString["error"], request.QueryString["error_description"]);
        if (!string.IsNullOrWhiteSpace(queryError))
        {
            _logger.LogWarning($"[GoogleAuth] OAuth error returned from Google: {queryError}");
            await SendResponseAsync(context, GetHtmlResponse("Login Failed", $"An error occurred: {queryError}", false), cancellationToken);
            return CallbackResult.CompletedFailure(queryError);
        }

        var queryState = request.QueryString["state"];
        if (string.IsNullOrWhiteSpace(queryState) || !string.Equals(queryState, expectedState, StringComparison.Ordinal))
        {
            _logger.LogWarning("[GoogleAuth] OAuth state mismatch detected.");
            await SendResponseAsync(context, GetHtmlResponse("Login Failed", "Google sign-in returned an invalid state value.", false), cancellationToken);
            return CallbackResult.CompletedFailure("Google sign-in returned an invalid state value.");
        }

        var queryCode = request.QueryString["code"];
        if (string.IsNullOrWhiteSpace(queryCode))
        {
            const string missingCodeMessage = "The browser callback did not include a Google authorization code.";
            _logger.LogWarning("[GoogleAuth] Callback completed without an authorization code.");
            await SendResponseAsync(context, GetHtmlResponse("Login Failed", missingCodeMessage, false), cancellationToken);
            return CallbackResult.CompletedFailure(missingCodeMessage);
        }

        await SendResponseAsync(context, GetHtmlResponse("Login Successful", "You have successfully logged in. You may now close this tab and return to the application.", true), cancellationToken);
        _logger.LogInformation("[GoogleAuth] OAuth authorization code callback received.");
        return CallbackResult.CompletedSuccess(queryCode);
    }

    private GoogleClientConfiguration GetRequiredGoogleClientConfiguration()
    {
        var inlineConfiguredOAuthValue = GetEnvironmentVariable(
            "LIBREGUARD_GOOGLE_OAUTH_CONFIG",
            "GOOGLE_OAUTH_CONFIG");
        var environmentClientId = GetEnvironmentVariable(
            "LIBREGUARD_GOOGLE_CLIENT_ID",
            "GOOGLE_DESKTOP_CLIENT_ID",
            "GOOGLE_NATIVE_CLIENT_ID",
            "GOOGLE_WINDOWS_CLIENT_ID",
            "GOOGLE_CLIENT_ID",
            "Authentication__Google__ClientId",
            "GOOGLE_WEB_CLIENT_ID",
            "GOOGLE_ANDROID_CLIENT_ID");

        if (!string.IsNullOrWhiteSpace(inlineConfiguredOAuthValue) && LooksLikeGoogleClientId(inlineConfiguredOAuthValue))
        {
            _logger.LogInformation("[GoogleAuth] Using Google OAuth client ID from LIBREGUARD_GOOGLE_OAUTH_CONFIG/GOOGLE_OAUTH_CONFIG.");
            return new GoogleClientConfiguration
            {
                ClientId = inlineConfiguredOAuthValue.Trim()
            };
        }

        if (!string.IsNullOrWhiteSpace(environmentClientId))
        {
            _logger.LogInformation("[GoogleAuth] Loaded Google OAuth client ID from environment variables.");
            return new GoogleClientConfiguration
            {
                ClientId = environmentClientId.Trim()
            };
        }

        foreach (var path in EnumerateGoogleConfigPaths())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                var parsed = JsonSerializer.Deserialize<GoogleClientConfigurationFile>(json);
                var section = parsed?.Installed ?? parsed?.Web;
                var clientId = FirstNonEmpty(parsed?.ClientId, section?.ClientId);
                if (string.IsNullOrWhiteSpace(clientId))
                    continue;

                _logger.LogInformation($"[GoogleAuth] Loaded Google OAuth client ID from '{path}'.");
                return new GoogleClientConfiguration
                {
                    ClientId = clientId
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[GoogleAuth] Failed to load Google OAuth config from '{path}': {ex.Message}");
            }
        }

        var defaultConfigPath = Path.Combine(AppContext.BaseDirectory, GoogleConfigFileNames[0]);
        throw new InvalidOperationException($"Google sign-in is not configured. Set LIBREGUARD_GOOGLE_CLIENT_ID or place a Google OAuth client JSON file containing only a public clientId at '{defaultConfigPath}'.");
    }

    private static IEnumerable<string> EnumerateGoogleConfigPaths()
    {
        var configuredPath = GetEnvironmentVariable(
            "LIBREGUARD_GOOGLE_OAUTH_CONFIG",
            "GOOGLE_OAUTH_CONFIG");
        if (!string.IsNullOrWhiteSpace(configuredPath) && !LooksLikeGoogleClientId(configuredPath))
            yield return Environment.ExpandEnvironmentVariables(configuredPath.Trim());

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in EnumerateGoogleConfigDirectories())
        {
            foreach (var fileName in GoogleConfigFileNames)
            {
                var candidatePath = Path.Combine(directory, fileName);
                if (seenPaths.Add(candidatePath))
                    yield return candidatePath;
            }
        }
    }

    private static IEnumerable<string> EnumerateGoogleConfigDirectories()
    {
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseDirectory in GetBaseGoogleConfigDirectories())
        {
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
                continue;

            var current = new DirectoryInfo(baseDirectory);
            for (var depth = 0; current is not null && depth < 6; depth++)
            {
                if (seenDirectories.Add(current.FullName))
                    yield return current.FullName;

                current = current.Parent;
            }
        }
    }

    private static IEnumerable<string> GetBaseGoogleConfigDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return AppDomain.CurrentDomain.BaseDirectory;
        yield return Environment.CurrentDirectory;

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
                yield return processDirectory;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "LibreGuard VPN");
    }

    private static string? GetEnvironmentVariable(params string[] variableNames)
    {
        foreach (var variableName in variableNames)
        {
            if (string.IsNullOrWhiteSpace(variableName))
                continue;

            var processValue = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(processValue))
                return processValue;

            var userValue = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(userValue))
                return userValue;

            var machineValue = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(machineValue))
                return machineValue;
        }

        return null;
    }

    private static bool LooksLikeGoogleClientId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static string BuildGoogleAuthorizationUrl(string clientId, string redirectUri, string codeChallenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = GoogleScope,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["prompt"] = "select_account"
        };

        var encodedQuery = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"{GoogleAuthorizationUrl}?{encodedQuery}";
    }

    private static string CreateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string CreateCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string GetHtmlResponse(string title, string message, bool isSuccess)
    {
        var color = isSuccess ? "#4CAF50" : "#F44336";
        var iconSvg = isSuccess
            ? "<svg viewBox=\"0 0 64 64\" xmlns=\"http://www.w3.org/2000/svg\"><circle cx=\"32\" cy=\"32\" r=\"30\" fill=\"#4CAF50\" opacity=\"0.15\" /><path d=\"M24 34l6 6 10-14\" stroke=\"#4CAF50\" stroke-width=\"5\" fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>"
            : "<svg viewBox=\"0 0 64 64\" xmlns=\"http://www.w3.org/2000/svg\"><circle cx=\"32\" cy=\"32\" r=\"30\" fill=\"#F44336\" opacity=\"0.15\" /><path d=\"M22 22l20 20M42 22L22 42\" stroke=\"#F44336\" stroke-width=\"5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";

        return $@"
<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{title} - LibreGuard VPN</title>
    <style>
        body {{
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            background-color: #121212;
            color: #ffffff;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
        }}
        .container {{
            background-color: #1e1e1e;
            padding: 40px;
            border-radius: 12px;
            box-shadow: 0 8px 24px rgba(0, 0, 0, 0.5);
            text-align: center;
            max-width: 400px;
            width: 90%;
        }}
        .icon-circle {{
            width: 120px;
            height: 120px;
            border-radius: 50%;
            background-color: rgba(255, 255, 255, 0.04);
            margin: 0 auto 20px;
            display: flex;
            align-items: center;
            justify-content: center;
        }}
        .icon-circle svg {{
            width: 64px;
            height: 64px;
        }}
        h1 {{
            margin: 0 0 10px;
            font-size: 24px;
            font-weight: 600;
        }}
        p {{
            margin: 0 0 30px;
            color: #aaaaaa;
            line-height: 1.5;
        }}
        .btn {{
            background-color: {color};
            color: #ffffff;
            border: none;
            padding: 12px 24px;
            border-radius: 6px;
            font-size: 16px;
            cursor: pointer;
            transition: background-color 0.2s;
        }}
        .btn:hover {{
            filter: brightness(1.05);
        }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""icon-circle"">{iconSvg}</div>
        <h1>{title}</h1>
        <p>{message}</p>
        <button class=""btn"" onclick=""closeWindowOrTab()"">Close Tab</button>
    </div>
    <script>
        function closeWindowOrTab() {{
            if (window.opener !== null) {{
                window.close();
            }} else {{
                window.location = 'about:blank';
            }}
        }}

        setTimeout(() => {{
            closeWindowOrTab();
        }}, 3000);
    </script>
</body>
</html>";
    }

    private static async Task SendResponseAsync(HttpListenerContext context, string html, CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
    }

    private static int StartListenerOnFreePort(HttpListener listener)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var tcpListener = new TcpListener(IPAddress.Loopback, 0);
            tcpListener.Start();
            var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            tcpListener.Stop();

            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                return port;
            }
            catch (HttpListenerException)
            {
                // Port was taken between detection and binding; try another.
            }
        }

        throw new InvalidOperationException("Could not bind to a free port for the OAuth callback listener after multiple attempts.");
    }

    private sealed record CallbackResult(bool Completed, string? AuthorizationCode, string? ErrorMessage)
    {
        public static CallbackResult CompletedSuccess(string authorizationCode) => new(true, authorizationCode, null);
        public static CallbackResult CompletedFailure(string? errorMessage) => new(true, null, errorMessage);
    }

    public async Task LogoutAsync()
    {
        await Task.CompletedTask;
    }
}
