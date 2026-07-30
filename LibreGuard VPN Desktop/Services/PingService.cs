using System.Diagnostics;
using System.Net.Http;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Service for measuring latency to VPN servers via the /ping endpoint.
/// Sends plain GET requests to server hostnames and measures round-trip time.
/// </summary>
public sealed class PingService
{
    private readonly HttpClient _httpClient;
    private const int DefaultPingPort = 5001;
    private const int PingTimeoutMs = 5000;
    private Dictionary<string, int> _cachedLatencies = new();

    public PingService()
    {
        // Create a dedicated HttpClient for ping operations with shorter timeout
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true // Allow self-signed certs
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(PingTimeoutMs)
        };
    }

    /// <summary>
    /// Measures latency to a VPN server via GET /ping endpoint.
    /// Constructs URL: https://{hostname}:{port}/ping
    /// Returns the round-trip time in milliseconds, or -1 if unreachable.
    /// </summary>
    public async Task<int> MeasureLatencyAsync(string hostname, int? customPort = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return -1;

        var port = customPort ?? DefaultPingPort;
        var url = $"https://{hostname}:{port}/ping";

        try
        {
            var stopwatch = Stopwatch.StartNew();
            
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
                return -1;

            return (int)stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException)
        {
            return -1; // Timeout
        }
        catch (HttpRequestException)
        {
            return -1; // Connection error
        }
        catch (Exception)
        {
            return -1; // Other exception
        }
    }

    /// <summary>
    /// Measures latency to multiple servers concurrently.
    /// Each measurement has a 5-second timeout; entire operation has a separate timeout.
    /// </summary>
    public async Task<Dictionary<string, int>> MeasureLatenciesAsync(
        IEnumerable<(string Hostname, int? Port)> servers, 
        CancellationToken cancellationToken = default)
    {
        var serverList = servers.ToList();
        if (serverList.Count == 0)
            return [];

        var tasks = serverList
            .Select(async s => (s.Hostname, Latency: await MeasureLatencyAsync(s.Hostname, s.Port, cancellationToken)))
            .ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Some tasks may have faulted; we'll use partial results
        }

        var result = new Dictionary<string, int>();
        foreach (var task in tasks)
        {
            try
            {
                var (hostname, latency) = await task;
                if (!string.IsNullOrEmpty(hostname))
                {
                    result[hostname] = latency;
                    _cachedLatencies[hostname] = latency;
                }
            }
            catch
            {
                // Skip failed tasks
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the cached latencies from the last measurement.
    /// </summary>
    public Dictionary<string, int> GetCachedLatencies() => new(_cachedLatencies);
}
