using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// DNS preference service backed by the LibreGuard management API.
/// </summary>
internal sealed class ApiDnsSettingsService : IDnsSettingsService
{
    private const string SettingsEndpoint = "api/dns/settings";
    private readonly ApiHttpClientService _api;

    public ApiDnsSettingsService(ApiHttpClientService api)
    {
        _api = api;
    }

    public async Task<DnsPreferenceResponse?> GetPreferenceAsync(CancellationToken ct = default)
    {
        var preference = await _api.GetAsync<DnsPreferenceResponse>(SettingsEndpoint, ct);
        TracePreference("GET", preference);
        return preference;
    }

    public async Task<DnsPreferenceUpdateResult> SetAdBlockingAsync(bool enabled, CancellationToken ct = default)
    {
        try
        {
            using var response = await _api.PutRawAsync(
                SettingsEndpoint,
                new DnsPreferenceUpdateRequest { AdBlockingEnabled = enabled },
                ct);

            if (response.IsSuccessStatusCode)
            {
                var preference = await ApiHttpClientService.DeserializeAsync<DnsPreferenceResponse>(response, ct);
                TracePreference("PUT", preference);
                return preference is null
                    ? Failure("INVALID_RESPONSE", "The DNS settings response was empty. Please try again.")
                    : new DnsPreferenceUpdateResult
                    {
                        Success = true,
                        Preference = preference
                    };
            }

            DnsPreferenceErrorResponse? error = null;
            try
            {
                error = await ApiHttpClientService.DeserializeAsync<DnsPreferenceErrorResponse>(response, ct);
            }
            catch (JsonException)
            {
                // A gateway or proxy may return a non-JSON error page. Map it below.
            }

            if (response.StatusCode == HttpStatusCode.Forbidden &&
                string.Equals(error?.ErrorCode, "PRO_REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                return new DnsPreferenceUpdateResult
                {
                    Success = false,
                    Preference = error?.Settings,
                    ErrorCode = "PRO_REQUIRED",
                    Message = error?.Message
                };
            }

            if ((int)response.StatusCode >= 500)
            {
                return Failure(
                    error?.ErrorCode ?? "SERVER_ERROR",
                    error?.Message ?? "DNS settings are temporarily unavailable. Please try again.",
                    error?.Settings);
            }

            return Failure(
                error?.ErrorCode ?? $"HTTP_{(int)response.StatusCode}",
                error?.Message ?? "Unable to update DNS settings. Please try again.",
                error?.Settings);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(
                "TIMEOUT",
                "The DNS settings request timed out. Please try again.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Failure(
                "NETWORK_ERROR",
                "Unable to reach the DNS settings service. Check your connection and try again.");
        }
        catch (JsonException)
        {
            return Failure("INVALID_RESPONSE", "The DNS settings response was invalid. Please try again.");
        }
    }

    private static void TracePreference(string method, DnsPreferenceResponse? preference)
    {
        if (preference is null)
        {
            Debug.WriteLine($"[ApiDnsSettingsService] {method} returned an empty DNS preference response.");
            return;
        }

        Debug.WriteLine(
            $"[ApiDnsSettingsService] {method} state: " +
            $"requested={preference.RequestedEnabled}, " +
            $"canUse={preference.CanUseAdBlocking}, " +
            $"effective={preference.EffectiveEnabled}, " +
            $"mode={preference.EffectiveMode}, " +
            $"propagationSeconds={preference.PropagationSeconds}");
    }

    private static DnsPreferenceUpdateResult Failure(
        string errorCode,
        string message,
        DnsPreferenceResponse? preference = null) =>
        new()
        {
            Success = false,
            Preference = preference,
            ErrorCode = errorCode,
            Message = message
        };
}
