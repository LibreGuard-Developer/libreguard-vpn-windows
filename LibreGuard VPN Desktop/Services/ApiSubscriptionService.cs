using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Provides subscription status and data usage quota from the management API.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// Gets the user's subscription status including plan, device count, and period info.
    /// </summary>
    Task<SubscriptionStatusResponse?> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the user's data usage quota for the current billing cycle.
    /// </summary>
    Task<DataQuotaResponse?> GetQuotaAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks whether the user is allowed to connect based on data limits.
    /// </summary>
    Task<CanConnectResponse?> CanConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates whether the current JWT token is still valid (not revoked).
    /// </summary>
    Task<bool> ValidateTokenAsync(CancellationToken ct = default);

    Task<MoneroPriceResponse?> GetMoneroPriceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default);
    Task<MoneroInvoiceResponse?> CreateMoneroInvoiceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default);
    Task<MoneroStatusResponse?> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken ct = default);
    Task<MoneroInvoiceResponse?> GetLatestMoneroInvoiceAsync(CancellationToken ct = default);

    Task<CreemCheckoutResponse?> CreateCreemCheckoutAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default);
    Task<CreemPaymentStatusResponse?> GetCreemPaymentStatusAsync(string transactionId, CancellationToken ct = default);
    Task<CreemPaymentVerifyResponse?> VerifyCreemPaymentAsync(string transactionId, CancellationToken ct = default);
}

/// <summary>
/// Subscription service backed by the LibreGuard management API.
/// </summary>
internal sealed class ApiSubscriptionService : ISubscriptionService
{
    private readonly ApiHttpClientService _api;
    private readonly TokenStorageService _tokenStorage;

    public ApiSubscriptionService(ApiHttpClientService api, TokenStorageService tokenStorage)
    {
        _api = api;
        _tokenStorage = tokenStorage;
    }

    public async Task<SubscriptionStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await _api.GetAsync<SubscriptionStatusResponse>("api/subscription/status", ct);
            if (status is not null)
            {
                _tokenStorage.UpdatePlanType(status.IsPro ? "Pro" : "Free");
            }

            return status;
        }
        catch
        {
            return null;
        }
    }

    public async Task<DataQuotaResponse?> GetQuotaAsync(CancellationToken ct = default)
    {
        try
        {
            return await _api.GetAsync<DataQuotaResponse>("api/usage/quota", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<CanConnectResponse?> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            return await _api.GetAsync<CanConnectResponse>("api/usage/can-connect", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ValidateTokenAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _api.GetAsync<TokenValidityResponse>("api/token/check", ct);
            return response?.IsValid ?? false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<MoneroPriceResponse?> GetMoneroPriceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default)
    {
        try
        {
            var query = cycle == BillingCycle.Yearly ? "?billingCycle=Yearly" : string.Empty;
            return await _api.GetAsync<MoneroPriceResponse>($"api/monero/price{query}", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<MoneroInvoiceResponse?> CreateMoneroInvoiceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default)
    {
        try
        {
            return await _api.PostAsync<MoneroInvoiceResponse>("api/monero/create-invoice", new { billingCycle = (int)cycle }, ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<MoneroStatusResponse?> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken ct = default)
    {
        try
        {
            return await _api.GetAsync<MoneroStatusResponse>($"api/monero/status/{invoiceId}", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<MoneroInvoiceResponse?> GetLatestMoneroInvoiceAsync(CancellationToken ct = default)
    {
        try
        {
            return await _api.GetAsync<MoneroInvoiceResponse>("api/monero/latest-invoice", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<CreemCheckoutResponse?> CreateCreemCheckoutAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default)
    {
        try
        {
            using var response = await _api.PostRawAsync("api/checkout/card", new { billingCycle = (int)cycle }, ct);
            if (response.IsSuccessStatusCode)
            {
                return await ApiHttpClientService.DeserializeAsync<CreemCheckoutResponse>(response, ct);
            }

            var error = await ApiHttpClientService.DeserializeAsync<ApiErrorResponse>(response, ct);
            return new CreemCheckoutResponse
            {
                ErrorCode = error?.ErrorCode,
                Message = error?.Message
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<CreemPaymentStatusResponse?> GetCreemPaymentStatusAsync(string transactionId, CancellationToken ct = default)
    {
        try
        {
            return await _api.GetAsync<CreemPaymentStatusResponse>($"api/payment/status/{transactionId}", ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<CreemPaymentVerifyResponse?> VerifyCreemPaymentAsync(string transactionId, CancellationToken ct = default)
    {
        try
        {
            return await _api.PostAsync<CreemPaymentVerifyResponse>("api/payment/verify", new { transactionId }, ct);
        }
        catch
        {
            return null;
        }
    }
}
