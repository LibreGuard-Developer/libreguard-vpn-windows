using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Models.Api;

/// <summary>
/// JSON response from GET /api/subscription/status.
/// </summary>
public sealed record SubscriptionStatusResponse
{
    [JsonPropertyName("plan")]
    public string Plan { get; init; } = "Free";

    [JsonPropertyName("isPro")]
    public bool IsPro { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "Active";

    [JsonPropertyName("currentPeriodEnd")]
    public DateTime? CurrentPeriodEnd { get; init; }

    [JsonPropertyName("cancelAtPeriodEnd")]
    public bool CancelAtPeriodEnd { get; init; }

    [JsonPropertyName("activeDevices")]
    public int ActiveDevices { get; init; }

    [JsonPropertyName("maxDevices")]
    public int MaxDevices { get; init; }

    [JsonPropertyName("canAddDevice")]
    public bool CanAddDevice { get; init; }

    [JsonPropertyName("billingCycle")]
    public string? BillingCycle { get; init; }
}

/// <summary>
/// JSON response from GET /api/usage/quota.
/// </summary>
public sealed record DataQuotaResponse
{
    [JsonPropertyName("bytesUsed")]
    public long BytesUsed { get; init; }

    [JsonPropertyName("bytesLimit")]
    public long? BytesLimit { get; init; }

    [JsonPropertyName("bytesRemaining")]
    public long? BytesRemaining { get; init; }

    [JsonPropertyName("usagePercentage")]
    public double? UsagePercentage { get; init; }

    [JsonPropertyName("isUnlimited")]
    public bool IsUnlimited { get; init; }

    [JsonPropertyName("isOverLimit")]
    public bool IsOverLimit { get; init; }

    [JsonPropertyName("formattedUsed")]
    public string? FormattedUsed { get; init; }

    [JsonPropertyName("formattedLimit")]
    public string? FormattedLimit { get; init; }

    [JsonPropertyName("formattedRemaining")]
    public string? FormattedRemaining { get; init; }

    [JsonPropertyName("cycleStart")]
    public DateTime? CycleStart { get; init; }

    [JsonPropertyName("cycleEnd")]
    public DateTime? CycleEnd { get; init; }

    [JsonPropertyName("resetDate")]
    public DateTime? ResetDate { get; init; }
}

/// <summary>
/// JSON response from GET /api/usage/can-connect.
/// </summary>
public sealed record CanConnectResponse
{
    [JsonPropertyName("allowed")]
    public bool Allowed { get; init; } = true;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("bytesUsed")]
    public long BytesUsed { get; init; }

    [JsonPropertyName("bytesLimit")]
    public long? BytesLimit { get; init; }

    [JsonPropertyName("resetDate")]
    public DateTime? ResetDate { get; init; }

    [JsonPropertyName("isUnlimited")]
    public bool IsUnlimited { get; init; }
}

public sealed record MoneroPriceResponse
{
    [JsonPropertyName("xmrAmount")]
    public decimal XmrAmount { get; init; }

    [JsonPropertyName("usdAmount")]
    public decimal UsdAmount { get; init; }

    [JsonPropertyName("xmrPriceUsd")]
    public decimal XmrPriceUsd { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }
}

public sealed record MoneroInvoiceResponse
{
    [JsonPropertyName("invoiceId")]
    public string? InvoiceId { get; init; }

    [JsonPropertyName("paymentAddress")]
    public string? PaymentAddress { get; init; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("billingCycle")]
    public string? BillingCycle { get; init; }
}

public sealed record MoneroStatusResponse
{
    [JsonPropertyName("invoiceId")]
    public string? InvoiceId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("amountRequired")]
    public decimal AmountRequired { get; init; }

    [JsonPropertyName("amountReceived")]
    public decimal AmountReceived { get; init; }

    [JsonPropertyName("confirmations")]
    public int Confirmations { get; init; }

    [JsonPropertyName("requiredConfirmations")]
    public int RequiredConfirmations { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; init; }

    [JsonPropertyName("billingCycle")]
    public string? BillingCycle { get; init; }
}

public sealed record CreemCheckoutResponse
{
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("checkoutUrl")]
    public string? CheckoutUrl { get; init; }

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; init; }

    [JsonPropertyName("localTransactionId")]
    public int LocalTransactionId { get; init; }

    [JsonPropertyName("amountUsd")]
    public decimal AmountUsd { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }
}

public sealed record CreemPaymentStatusResponse
{
    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("amountRequired")]
    public decimal AmountRequired { get; init; }

    [JsonPropertyName("amountReceived")]
    public decimal AmountReceived { get; init; }

    [JsonPropertyName("confirmedAt")]
    public DateTime? ConfirmedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; init; }

    [JsonPropertyName("serverTime")]
    public DateTime ServerTime { get; init; }
}

public sealed record CreemPaymentVerifyResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("subscription")]
    public CreemVerifiedSubscription? Subscription { get; init; }

    [JsonPropertyName("serverTime")]
    public DateTime ServerTime { get; init; }
}

public sealed record CreemVerifiedSubscription
{
    [JsonPropertyName("isPro")]
    public bool IsPro { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; init; }

    [JsonPropertyName("billingCycle")]
    public string? BillingCycle { get; init; }
}

/// <summary>
/// JSON response from GET /api/token/check.
/// </summary>
internal sealed record TokenValidityResponse
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
