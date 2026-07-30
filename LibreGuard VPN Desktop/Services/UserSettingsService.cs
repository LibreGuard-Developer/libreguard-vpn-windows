using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Persists user settings to a local JSON file.
/// </summary>
public sealed class UserSettingsService : IUserSettingsService
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibreGuardVPN");
    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "user_settings.json");

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private UserSettings _settings = new();

    public UserSettings Settings => _settings;

    public event EventHandler? SettingsChanged;

    public UserSettingsService()
    {
        // Sync load on startup is usually acceptable for small settings files
        LoadSettingsSync();
    }

    public async Task SaveSettingsAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!Directory.Exists(SettingsDirectory))
            {
                Directory.CreateDirectory(SettingsDirectory);
            }

            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(SettingsFilePath, json);
        }
        finally
        {
            _fileLock.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadSettingsAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = await File.ReadAllTextAsync(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<UserSettings>(json);
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
        }
        catch
        {
            // Fallback to defaults on error
            _settings = new UserSettings();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void LoadSettingsSync()
    {
        _fileLock.Wait();
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<UserSettings>(json);
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
        }
        catch
        {
            _settings = new UserSettings();
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
