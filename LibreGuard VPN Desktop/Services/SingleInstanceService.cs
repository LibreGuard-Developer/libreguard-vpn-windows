using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace LibreGuard_VPN_Desktop.Services;

public class SingleInstanceService : IDisposable
{
    private const string MutexName = "LibreGuardVPN_SingleInstance_Mutex";
    private const string PipeName = "LibreGuardVPN_DeepLink_Pipe";
    private const string UriScheme = "libreguardvpn";
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public event EventHandler<string>? DeepLinkReceived;

    public void RegisterUriScheme()
    {
        try
        {
            var appPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(appPath)) return;

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{UriScheme}");
            key.SetValue("", "URL:LibreGuard VPN Protocol");
            key.SetValue("URL Protocol", "");

            using var defaultIcon = key.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue("", $"{appPath},1");

            using var commandKey = key.CreateSubKey(@"shell\open\command");
            commandKey.SetValue("", $"\"{appPath}\" \"%1\"");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to register URI scheme: {ex.Message}");
        }
    }

    public bool IsFirstInstance()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        return createdNew;
    }

    public void StartListening()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ListenForDeepLinks(_cts.Token));
    }

    private async Task ListenForDeepLinks(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);

                using var reader = new System.IO.StreamReader(server);
                var message = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(message))
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        DeepLinkReceived?.Invoke(this, message);
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error listening for deep links: {ex.Message}");
            }
        }
    }

    public async Task SendDeepLinkToRunningInstanceAsync(string deepLink)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);

            using var writer = new System.IO.StreamWriter(client);
            await writer.WriteLineAsync(deepLink);
            await writer.FlushAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error sending deep link to running instance: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
