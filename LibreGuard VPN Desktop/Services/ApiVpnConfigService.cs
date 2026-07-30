using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Retrieves VPN configuration and credentials from the LibreGuard management API.
/// Uses ApiHttpClientService for Bearer auth with automatic token refresh.
/// </summary>
internal sealed class ApiVpnConfigService : IVpnConfigService
{
    private readonly ApiHttpClientService _api;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ApiVpnConfigService(ApiHttpClientService api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <inheritdoc />
    public async Task<VpnConfigResponse?> GetConfigAsync(int serverId, string protocol, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protocol);

        try
        {
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] GetConfigAsync START: serverId={serverId}, protocol={protocol}");

            var request = new VpnConfigRequest
            {
                ServerId = serverId,
                Protocol = protocol
            };

            // Use PostRawAsync to handle 404 cleanly
            using var response = await _api.PostRawAsync("api/vpn/config", request, ct);
            return await HandleConfigResponseAsync(response, request, serverId, protocol, retryDeviceKeyRegistration: true, ct);
        }
        catch (OperationCanceledException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] GetConfigAsync CANCELLED: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] GetConfigAsync ERROR: {ex.GetType().Name} - {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] Stack Trace: {ex.StackTrace}");
            throw;
        }
    }

    private async Task<VpnConfigResponse?> HandleConfigResponseAsync(
        HttpResponseMessage response,
        VpnConfigRequest request,
        int serverId,
        string protocol,
        bool retryDeviceKeyRegistration,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var config = await response.Content.ReadFromJsonAsync<VpnConfigResponse>(JsonOptions, ct);
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] GetConfigAsync SUCCESS: CertificateName={config?.CertificateName}");
            return config;
        }

        var error = await CreateRequestExceptionAsync(response, ct);
        System.Diagnostics.Debug.WriteLine(
            $"[ApiVpnConfigService] GetConfigAsync FAILED: Status={error.StatusCode}, ErrorCode={error.ErrorCode ?? "(none)"}, Message={error.BackendMessage ?? "(none)"}");

        if (response.StatusCode == HttpStatusCode.Conflict &&
            retryDeviceKeyRegistration &&
            string.Equals(error.ErrorCode, "DEVICE_KEY_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine("[ApiVpnConfigService] Device key missing on backend. Forcing refresh/key registration and retrying config request...");
            if (await _api.TryRefreshTokenAsync(ct, force: true))
            {
                using var retryResponse = await _api.PostRawAsync("api/vpn/config", request, ct);
                return await HandleConfigResponseAsync(retryResponse, request, serverId, protocol, retryDeviceKeyRegistration: false, ct);
            }
        }

        if (response.StatusCode == HttpStatusCode.NotFound && IsMissingCertificateError(error))
        {
            System.Diagnostics.Debug.WriteLine("[ApiVpnConfigService] Certificate missing. Initiating certificate request flow...");

            var certJobId = await RequestCertificateAsync(serverId, protocol, ct);
            if (certJobId.HasValue && await PollCertificateJobAsync(certJobId.Value, ct))
            {
                System.Diagnostics.Debug.WriteLine("[ApiVpnConfigService] Certificate generation successful. Retrying config request...");
                using var retryResponse = await _api.PostRawAsync("api/vpn/config", request, ct);
                return await HandleConfigResponseAsync(retryResponse, request, serverId, protocol, retryDeviceKeyRegistration, ct);
            }
        }

        throw error;
    }

    private async Task<int?> RequestCertificateAsync(int serverId, string protocol, CancellationToken ct)
    {
        try
        {
            var request = new CertificateRequest { ServerId = serverId, VpnType = protocol };
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] Requesting certificate for ServerId={serverId}, Protocol={protocol}");
            
            using var response = await _api.PostRawAsync("api/certificates/request", request, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await CreateRequestExceptionAsync(response, ct);
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiVpnConfigService] Certificate request failed: Status={error.StatusCode}, ErrorCode={error.ErrorCode ?? "(none)"}, Message={error.BackendMessage ?? "(none)"}");
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CertificateRequestResponse>(JsonOptions, ct);
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] Certificate Job Created: ID={result?.JobId}, Status={result?.Status}");
            return result?.JobId;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] RequestCertificateAsync ERROR: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> PollCertificateJobAsync(int jobId, CancellationToken ct)
    {
        // Polling parameters: Max 30 seconds, check every 2 seconds
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(30);
        
        while (DateTime.UtcNow - startTime < timeout)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            try
            {
                System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] Polling Job {jobId}...");
                var job = await _api.GetAsync<CertificateJobResponse>($"api/certificates/jobs/{jobId}", ct);

                if (job is null) continue;

                System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] Job {jobId} Status: {job.Status}");

                if (string.Equals(job.Status, "Success", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                
                if (string.Equals(job.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] Job {jobId} failed: {job.ErrorMessage}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] PollCertificateJobAsync ERROR: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] Job {jobId} timed out");
        return false;
    }

    private static bool IsMissingCertificateError(VpnConfigRequestException error)
    {
        return (error.BackendMessage?.Contains("certificate", StringComparison.OrdinalIgnoreCase) ?? false)
            || (error.RawBody?.Contains("certificate", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    internal static async Task<VpnConfigRequestException> CreateRequestExceptionAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        string? rawBody = null;
        ApiErrorResponse? error = null;

        try
        {
            rawBody = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiVpnConfigService] Error response body: {rawBody.Substring(0, Math.Min(500, rawBody.Length))}");
                error = JsonSerializer.Deserialize<ApiErrorResponse>(rawBody, JsonOptions);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] Failed to parse error response body: {ex.Message}");
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && error?.RetryAfterSeconds is > 0)
            retryAfter = TimeSpan.FromSeconds(error.RetryAfterSeconds.Value);

        return new VpnConfigRequestException(
            response.StatusCode,
            error?.Message,
            error?.ErrorCode,
            retryAfter,
            rawBody);
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadOpenVpnConfigAsync(int serverId, CancellationToken ct = default)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] DownloadOpenVpnConfigAsync START: serverId={serverId}");

            // POST /api/vpn/config/openvpn/download { "serverId": N }
            // Server returns binary .ovpn file (Content-Type: application/x-openvpn-profile) for Pro users
            var body = new { serverId };
            using var response = await _api.PostRawAsync("api/vpn/config/openvpn/download", body, ct);

            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] DownloadOpenVpnConfigAsync SUCCESS: {bytes.Length} bytes");
                return bytes;
            }

            // Map specific HTTP errors to actionable messages for upstream callers
            switch ((int)response.StatusCode)
            {
                case 401:
                    System.Diagnostics.Debug.WriteLine("[ApiVpnConfigService] DownloadOpenVpnConfigAsync: 401 Unauthorized");
                    break;
                case 403:
                    System.Diagnostics.Debug.WriteLine("[ApiVpnConfigService] DownloadOpenVpnConfigAsync: 403 Forbidden - Pro subscription required");
                    break;
                case 404:
                    System.Diagnostics.Debug.WriteLine("[ApiVpnConfigService] DownloadOpenVpnConfigAsync: 404 Not Found - config unavailable");
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] DownloadOpenVpnConfigAsync: Failed with {response.StatusCode}");
                    break;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiVpnConfigService] DownloadOpenVpnConfigAsync ERROR: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }
}
