using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class DeepLinkParserTests
{
    [Fact]
    public void TryParse_AppResetLink_ReturnsResetPasswordPayload()
    {
        var deepLink = "libreguardvpn://account/reset-password?email=user%40example.com&code=abc123";

        var result = DeepLinkParser.TryParse(deepLink, out var payload);

        Assert.True(result);
        Assert.Equal(DeepLinkAction.ResetPassword, payload.Action);
        Assert.Equal("user@example.com", payload.Email);
        Assert.Equal("abc123", payload.Token);
    }

    [Fact]
    public void TryParse_BackendAndroidResetPage_ReturnsResetPasswordPayload()
    {
        var deepLink = "https://shadowlink-vpn-ca-proxy1.ddns.net/External/AndroidAppPasswordReset?code=encoded-token&email=user%40example.com";

        var result = DeepLinkParser.TryParse(deepLink, out var payload);

        Assert.True(result);
        Assert.Equal(DeepLinkAction.ResetPassword, payload.Action);
        Assert.Equal("user@example.com", payload.Email);
        Assert.Equal("encoded-token", payload.Token);
    }

    [Fact]
    public void TryParse_WebResetPageWithTokenAlias_ReturnsResetPasswordPayload()
    {
        var deepLink = "https://shadowlink-vpn-ca-proxy1.ddns.net/Identity/Account/ResetPassword?token=encoded-token&email=user%40example.com";

        var result = DeepLinkParser.TryParse(deepLink, out var payload);

        Assert.True(result);
        Assert.Equal(DeepLinkAction.ResetPassword, payload.Action);
        Assert.Equal("user@example.com", payload.Email);
        Assert.Equal("encoded-token", payload.Token);
    }

    [Fact]
    public void TryParse_EmailConfirmationLinkWithoutToken_ReturnsFalse()
    {
        var deepLink = "https://shadowlink-vpn-ca-proxy1.ddns.net/Identity/Account/ConfirmEmail?userId=user-1&code=abc123";

        var result = DeepLinkParser.TryParse(deepLink, out var payload);

        Assert.False(result);
        Assert.Equal(DeepLinkAction.None, payload.Action);
    }
}
