using System.Diagnostics;
using System.IO;
using LibreGuard.Common.Windows;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace LibreGuard_VPN_Desktop.Services;

internal sealed class WindowsNotificationIdentityService
{
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;

        AppIdentity.ApplyCurrentProcessAppUserModelId();
        Trace.WriteLine($"[Notifications] Applied AppUserModelID '{AppIdentity.AppUserModelId}'.");

        EnsureToastActivatorRegistration();
        EnsureCurrentUserShortcut();
        LogNotifierSetting();
    }

    private static void EnsureToastActivatorRegistration()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                Trace.WriteLine("[Notifications] Process path unavailable; skipping toast activator registration.");
                return;
            }

            using var clsidKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\CLSID\{{{AppIdentity.ToastActivatorClsid:D}}}\LocalServer32");
            clsidKey?.SetValue(string.Empty, $"\"{exePath}\"");

            Trace.WriteLine($"[Notifications] Registered toast activator CLSID '{AppIdentity.ToastActivatorClsid:D}'.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Notifications] Failed to register toast activator CLSID: {ex}");
        }
    }

    private static void EnsureCurrentUserShortcut()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                Trace.WriteLine("[Notifications] Process path unavailable; skipping Start Menu shortcut repair.");
                return;
            }

            var appDirectory = Path.GetDirectoryName(exePath)!;
            var iconPath = Path.Combine(appDirectory, "LibreGuard_logo_cropped_V3.ico");
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(appDirectory, "Assets", "Images", "LibreGuard_logo_cropped_V3.ico");
            if (!File.Exists(iconPath))
                iconPath = exePath;

            var programsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                AppIdentity.ShortcutFolderName);
            var shortcutPath = Path.Combine(programsDirectory, AppIdentity.ShortcutFileName);

            ShellLinkUtility.CreateOrUpdateShortcut(
                shortcutPath,
                exePath,
                appDirectory,
                iconPath,
                AppIdentity.AppUserModelId,
                AppIdentity.ToastActivatorClsid);

            Trace.WriteLine($"[Notifications] Ensured Start Menu shortcut '{shortcutPath}' with AppUserModelID.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Notifications] Failed to repair Start Menu shortcut: {ex}");
        }
    }

    private static void LogNotifierSetting()
    {
        try
        {
            var setting = ToastNotificationManager.CreateToastNotifier(AppIdentity.AppUserModelId).Setting;
            Trace.WriteLine($"[Notifications] Toast notifier setting: {setting}.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Notifications] Unable to inspect toast notifier setting: {ex}");
        }
    }
}
