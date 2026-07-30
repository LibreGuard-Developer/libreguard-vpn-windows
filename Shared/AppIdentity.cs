using System.Runtime.InteropServices;

namespace LibreGuard.Common.Windows;

internal static class AppIdentity
{
    public const string AppUserModelId = "LibreGuard.VPN.Desktop";
    public const string AppDisplayName = "LibreGuard VPN";
    public const string ShortcutFolderName = "LibreGuard VPN";
    public const string ShortcutFileName = "LibreGuard VPN.lnk";
    public static readonly Guid ToastActivatorClsid = new("C173E6D5-12D4-4CDE-A5D3-9E9F6D7E4B32");

    public static void ApplyCurrentProcessAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // Best-effort only. Startup should not fail if Windows rejects the call.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
