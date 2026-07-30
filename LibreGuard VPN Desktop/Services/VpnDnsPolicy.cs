namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Client-side DNS policy applied to every VPN tunnel.
/// Resolver selection for features such as ad blocking happens on the VPN server.
/// </summary>
internal static class VpnDnsPolicy
{
    internal const string ResolverAddress = "10.254.0.53";

    internal static string[] CreateResolverList() => [ResolverAddress];
}
