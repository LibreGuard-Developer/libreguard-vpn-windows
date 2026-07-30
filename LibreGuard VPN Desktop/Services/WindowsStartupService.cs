using System.Diagnostics;
using Microsoft.Win32;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Manages the Windows startup registry entry so the app launches automatically at user login.
/// Uses HKCU\Software\Microsoft\Windows\CurrentVersion\Run to avoid requiring elevated privileges.
/// </summary>
internal static class WindowsStartupService
{
    private const string AppName = "LibreGuardVPN";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Registers or unregisters the app to launch at Windows startup.
    /// </summary>
    public static void SetLaunchAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (enable)
            {
                var exePath = Environment.ProcessPath
                              ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry access may be restricted in some environments; fail silently.
        }
    }

    /// <summary>
    /// Returns whether the app is currently registered to launch at startup.
    /// </summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }
}
