using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class CardCheckoutNavigationPolicyTests
{
    [Theory]
    [InlineData("https://checkout.creem.io/ch_test", true)]
    [InlineData("http://checkout.creem.io/ch_test", false)]
    [InlineData("file:///C:/checkout.html", false)]
    [InlineData("libreguard://checkout/complete", false)]
    public void IsAllowedWebUri_OnlyAllowsHttps(string value, bool expected)
    {
        Assert.Equal(expected, CardCheckoutNavigationPolicy.IsAllowedWebUri(new Uri(value)));
    }

    [Theory]
    [InlineData("https://management.libreguard.net/Billing/Card?success=1&checkout_id=ch_test", true)]
    [InlineData("https://management.libreguard.net/billing/card?checkout_id=ch_test", true)]
    [InlineData("https://management.libreguard.net/Billing/Card?canceled=1", false)]
    [InlineData("https://checkout.creem.io/ch_test", false)]
    [InlineData("http://management.libreguard.net/Billing/Card?success=1", false)]
    public void IsCheckoutReturn_DetectsOnlySecureSuccessReturn(string value, bool expected)
    {
        Assert.Equal(expected, CardCheckoutNavigationPolicy.IsCheckoutReturn(new Uri(value)));
    }
}
