using Microsoft.Web.WebView2.Core;
using System.IO;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Creates the WebView2 environment used for card checkout outside the installed application directory.
/// </summary>
internal static class CardCheckoutWebView2Environment
{
    private const string UserDataDirectoryName = "LibreGuardVPN";
    private const string WebView2DirectoryName = "WebView2";

    internal static string GetUserDataFolder() => GetUserDataFolder(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string GetUserDataFolder(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        return Path.Combine(localApplicationData, UserDataDirectoryName, WebView2DirectoryName);
    }

    internal static async Task<CoreWebView2Environment> CreateAsync()
    {
        var userDataFolder = GetUserDataFolder();
        Directory.CreateDirectory(userDataFolder);

        // Passing null selects the installed Evergreen WebView2 Runtime.
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder);
    }
}
