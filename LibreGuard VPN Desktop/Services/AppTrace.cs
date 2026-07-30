using System.Diagnostics;
using System.IO;
using System.Text;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Configures an always-on trace file so diagnostics are available from published builds.
/// </summary>
public static class AppTrace
{
    private static readonly object Sync = new();
    private static string? _tracePath;

    public static string Initialize()
    {
        lock (Sync)
        {
            if (_tracePath is not null)
                return _tracePath;

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LibreGuard VPN");
            Directory.CreateDirectory(folder);

            _tracePath = Path.Combine(folder, "debug.log");

            var stream = new FileStream(
                _tracePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            Trace.AutoFlush = true;
            Trace.Listeners.Add(new TextWriterTraceListener(writer, nameof(AppTrace)));
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [TRACE] Trace listener initialized at {_tracePath}");

            return _tracePath;
        }
    }
}
