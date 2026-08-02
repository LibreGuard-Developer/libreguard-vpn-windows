using System.Reflection;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Provides the application version format expected by the management API.
/// </summary>
internal static class AppVersionProvider
{
    private static readonly string ApiVersion = BuildApiVersion();

    public static string GetApiVersion() => ApiVersion;

    private static string BuildApiVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is not null
            ? $"Desktop/{version.Major}.{version.Minor}.{version.Build}"
            : "Desktop/1.1.1";
    }
}
