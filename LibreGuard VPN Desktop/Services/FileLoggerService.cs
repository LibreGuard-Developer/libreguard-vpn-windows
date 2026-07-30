using System.IO;

namespace LibreGuard_VPN_Desktop.Services;

public class FileLoggerService : ILoggerService
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public FileLoggerService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(appData, "LibreGuard VPN");
        Directory.CreateDirectory(folder);
        _logPath = Path.Combine(folder, "app.log");
    }

    public void LogInformation(string message) => Log("INFO", message);
    public void LogWarning(string message) => Log("WARN", message);
    public void LogError(string message, Exception? ex = null) => Log("ERROR", $"{message} {ex}");

    private void Log(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line);
            }
        }
        catch (Exception ex)
        {
            // Fallback to trace output so published builds still capture the failure.
            System.Diagnostics.Trace.WriteLine($"Failed to log to file: {ex.Message}");
        }
    }
}
