using System.Windows;
using LibreGuard_VPN_Desktop.Models;
using Microsoft.Win32;

namespace LibreGuard_VPN_Desktop.Services;

internal enum AppTheme
{
    Light,
    Dark
}

public interface ISystemThemeReader
{
    bool IsLightThemeEnabled();
}

public sealed class WindowsSystemThemeReader : ISystemThemeReader
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    public bool IsLightThemeEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
        var value = key?.GetValue(AppsUseLightThemeValue);
        return value is not int intValue || intValue != 0;
    }
}

public sealed class ThemeService : IThemeService
{
    private static readonly Uri LightThemeUri = new("Themes/LightTheme.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("Themes/DarkTheme.xaml", UriKind.Relative);

    private readonly ISystemThemeReader _systemThemeReader;

    public ThemeService(ISystemThemeReader systemThemeReader)
    {
        _systemThemeReader = systemThemeReader;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppThemePreference CurrentPreference { get; private set; } = AppThemePreference.System;

    internal AppTheme ResolveEffectiveTheme(AppThemePreference preference)
    {
        return preference switch
        {
            AppThemePreference.Light => AppTheme.Light,
            AppThemePreference.Dark => AppTheme.Dark,
            _ => _systemThemeReader.IsLightThemeEnabled() ? AppTheme.Light : AppTheme.Dark
        };
    }

    public Task ApplyPreferenceAsync(AppThemePreference preference)
    {
        CurrentPreference = preference;
        var effectiveTheme = ResolveEffectiveTheme(preference);
        ApplyTheme(effectiveTheme);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentPreference != AppThemePreference.System)
        {
            return;
        }

        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            _ = ApplyPreferenceAsync(CurrentPreference);
        }
    }

    private static void ApplyTheme(AppTheme theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var dispatcher = application.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            ReplaceThemeDictionary(application.Resources, theme);
        }
        else
        {
            dispatcher.Invoke(() => ReplaceThemeDictionary(application.Resources, theme));
        }
    }

    private static void ReplaceThemeDictionary(ResourceDictionary resources, AppTheme theme)
    {
        var targetSource = theme == AppTheme.Dark ? DarkThemeUri : LightThemeUri;

        for (var index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            var source = resources.MergedDictionaries[index].Source?.OriginalString;
            if (source is "Themes/LightTheme.xaml" or "Themes/DarkTheme.xaml")
            {
                resources.MergedDictionaries.RemoveAt(index);
            }
        }

        resources.MergedDictionaries.Insert(0, new ResourceDictionary { Source = targetSource });
    }
}
