using System;
using System.Collections.Specialized;
using System.Web;

namespace LibreGuard_VPN_Desktop.Services;

internal enum DeepLinkAction
{
    None = 0,
    LoginWithToken = 1,
    ResetPassword = 2,
    Shutdown = 3
}

internal readonly record struct DeepLinkPayload(
    DeepLinkAction Action,
    string? Token = null,
    string? Email = null);

internal static class DeepLinkParser
{
    private const string AppScheme = "libreguardvpn";

    public static bool TryParse(string? deepLink, out DeepLinkPayload payload)
    {
        payload = default;

        if (string.IsNullOrWhiteSpace(deepLink))
        {
            return false;
        }

        var normalized = deepLink.Trim().Trim('"');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return TryParseAppScheme(uri, out payload)
            || TryParseWebResetLink(uri, out payload);
    }

    private static bool TryParseAppScheme(Uri uri, out DeepLinkPayload payload)
    {
        payload = default;
        if (!uri.Scheme.Equals(AppScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);

        if (uri.Host.Equals("email", StringComparison.OrdinalIgnoreCase)
            && MatchesPath(uri, "/confirmed"))
        {
            var token = FirstNonEmpty(query, "token");
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            payload = new DeepLinkPayload(DeepLinkAction.LoginWithToken, Token: token);
            return true;
        }

        if (uri.Host.Equals("account", StringComparison.OrdinalIgnoreCase)
            && MatchesPath(uri, "/reset-password"))
        {
            return TryCreateResetPasswordPayload(query, out payload);
        }

        if (uri.Host.Equals("app", StringComparison.OrdinalIgnoreCase)
            && MatchesPath(uri, "/shutdown"))
        {
            payload = new DeepLinkPayload(DeepLinkAction.Shutdown);
            return true;
        }

        return false;
    }

    private static bool TryParseWebResetLink(Uri uri, out DeepLinkPayload payload)
    {
        payload = default;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!MatchesPath(uri, "/External/AndroidAppPasswordReset")
            && !MatchesPath(uri, "/Identity/Account/ResetPassword"))
        {
            return false;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        return TryCreateResetPasswordPayload(query, out payload);
    }

    private static bool TryCreateResetPasswordPayload(NameValueCollection query, out DeepLinkPayload payload)
    {
        payload = default;

        var email = FirstNonEmpty(query, "email");
        var token = FirstNonEmpty(query, "code", "token", "resetToken");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        payload = new DeepLinkPayload(
            DeepLinkAction.ResetPassword,
            Token: token,
            Email: email);
        return true;
    }

    private static bool MatchesPath(Uri uri, string expectedPath)
    {
        return uri.AbsolutePath.TrimEnd('/').Equals(expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(NameValueCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = query[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
