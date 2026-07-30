using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class AccountPlanServiceTests
{
    [Fact]
    public void Constructor_WithCachedPro_StartsAsPro()
    {
        var service = new AccountPlanService(
            new StaticAuthenticationService(UserPlan.Pro),
            new StaticSubscriptionService(null));

        Assert.Equal(UserPlan.Pro, service.CurrentPlan);
        Assert.True(service.IsPro);
        Assert.True(service.IsOpenVpnAvailable);
        Assert.Equal("Pro", service.CurrentPlanLabel);
    }

    [Fact]
    public async Task RefreshAsync_WithCachedFreeAndBackendPro_UpdatesToPro()
    {
        var service = new AccountPlanService(
            new StaticAuthenticationService(UserPlan.Free),
            new StaticSubscriptionService(new SubscriptionStatusResponse { IsPro = true, Plan = "Pro", BillingCycle = "Monthly" }));

        await service.RefreshAsync(force: true);

        Assert.Equal(UserPlan.Pro, service.CurrentPlan);
        Assert.True(service.IsOpenVpnAvailable);
        Assert.Equal("Pro (Monthly)", service.CurrentPlanLabel);
    }

    [Fact]
    public async Task RefreshAsync_WhenStatusUnavailable_KeepsCachedPro()
    {
        var service = new AccountPlanService(
            new StaticAuthenticationService(UserPlan.Pro),
            new StaticSubscriptionService(null));

        await service.RefreshAsync(force: true);

        Assert.Equal(UserPlan.Pro, service.CurrentPlan);
        Assert.True(service.IsOpenVpnAvailable);
    }

    [Fact]
    public async Task RefreshAsync_WithBackendFree_DowngradesCachedPro()
    {
        var service = new AccountPlanService(
            new StaticAuthenticationService(UserPlan.Pro),
            new StaticSubscriptionService(new SubscriptionStatusResponse { IsPro = false, Plan = "Free" }));

        await service.RefreshAsync(force: true);

        Assert.Equal(UserPlan.Free, service.CurrentPlan);
        Assert.False(service.IsOpenVpnAvailable);
        Assert.Equal("Free", service.CurrentPlanLabel);
    }

    private sealed class StaticAuthenticationService : IAuthenticationService
    {
        public StaticAuthenticationService(UserPlan plan)
        {
            Plan = plan;
        }

        public event Action? SessionChanged;
        public bool IsAuthenticated => true;
        public string? UserEmail => "plan@example.test";
        public string? UserId => "user-1";
        public UserPlan Plan { get; }

        public Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> Verify2FaAsync(string email, string code, string? pendingLoginToken = null, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithGoogleAsync(GoogleLoginContext loginContext, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithOAuthAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithTokenAsync(string token, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<bool> CheckEmailConfirmationAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ResendConfirmationAsync(string email, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthAsync(string email, string password, int deviceIdToRemove, CancellationToken cancellationToken = default) => Task.FromResult(PreAuthDeviceRemovalResult.Ok());
        public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthOAuthAsync(GoogleLoginContext loginContext, string provider, int deviceIdToRemove, CancellationToken cancellationToken = default) => Task.FromResult(PreAuthDeviceRemovalResult.Ok());
        public Task<TwoFactorSetupResponse?> InitiateTwoFactorSetupAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorSetupResponse?>(null);
        public Task<TwoFactorEnableResponse?> VerifyAndEnableTwoFactorAsync(string code, CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorEnableResponse?>(null);
        public Task<TwoFactorRecoveryCodesResponse?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorRecoveryCodesResponse?>(null);
        public Task<TwoFactorDisableResponse?> DisableTwoFactorAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorDisableResponse?>(null);
        public Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PasswordResetResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default) => Task.FromResult(PasswordResetResult.Ok("ok"));
        public Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorStatusResponse?>(null);
        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            SessionChanged?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class StaticSubscriptionService : ISubscriptionService
    {
        private readonly SubscriptionStatusResponse? _status;

        public StaticSubscriptionService(SubscriptionStatusResponse? status)
        {
            _status = status;
        }

        public Task<SubscriptionStatusResponse?> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(_status);
        public Task<DataQuotaResponse?> GetQuotaAsync(CancellationToken ct = default) => Task.FromResult<DataQuotaResponse?>(null);
        public Task<CanConnectResponse?> CanConnectAsync(CancellationToken ct = default) =>
            Task.FromResult<CanConnectResponse?>(new CanConnectResponse { Allowed = true });
        public Task<bool> ValidateTokenAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MoneroPriceResponse?> GetMoneroPriceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) => Task.FromResult<MoneroPriceResponse?>(null);
        public Task<MoneroInvoiceResponse?> CreateMoneroInvoiceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) => Task.FromResult<MoneroInvoiceResponse?>(null);
        public Task<MoneroStatusResponse?> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken ct = default) => Task.FromResult<MoneroStatusResponse?>(null);
        public Task<MoneroInvoiceResponse?> GetLatestMoneroInvoiceAsync(CancellationToken ct = default) => Task.FromResult<MoneroInvoiceResponse?>(null);
        public Task<CreemCheckoutResponse?> CreateCreemCheckoutAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) => Task.FromResult<CreemCheckoutResponse?>(null);
        public Task<CreemPaymentStatusResponse?> GetCreemPaymentStatusAsync(string transactionId, CancellationToken ct = default) => Task.FromResult<CreemPaymentStatusResponse?>(null);
        public Task<CreemPaymentVerifyResponse?> VerifyCreemPaymentAsync(string transactionId, CancellationToken ct = default) => Task.FromResult<CreemPaymentVerifyResponse?>(null);
    }
}
