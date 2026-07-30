using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Tests.ViewModels;

public sealed class UpgradeViewModelTests
{
    [Fact]
    public async Task SelectCardAsync_WithCheckout_StoresTransactionAndOpensEmbeddedCheckout()
    {
        var subscription = new RecordingSubscriptionService
        {
            Checkout = new CreemCheckoutResponse
            {
                CheckoutUrl = "https://checkout.creem.test/session",
                TransactionId = "ch_test_123"
            }
        };
        var presenter = new RecordingCardCheckoutPresenter();
        var viewModel = CreateViewModel(subscription, presenter: presenter);
        string? openedUrl = null;
        viewModel.OpenUrl = url =>
        {
            openedUrl = url;
            return true;
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCardSelected);
        Assert.True(viewModel.IsCardPaymentPending);
        Assert.False(viewModel.IsCardPaymentFailed);
        Assert.Equal("ch_test_123", viewModel.CardTransactionId);
        Assert.Equal("https://checkout.creem.test/session", viewModel.CardCheckoutUrl);
        Assert.Equal(1, presenter.ShowCount);
        Assert.Equal("https://checkout.creem.test/session", presenter.LastUri?.AbsoluteUri);
        Assert.Null(openedUrl);
    }

    [Fact]
    public async Task OpenCheckoutInBrowserCommand_UsesExistingCheckoutUrl()
    {
        var subscription = CheckoutSubscription();
        var viewModel = CreateViewModel(subscription);
        string? openedUrl = null;
        viewModel.OpenUrl = url =>
        {
            openedUrl = url;
            return true;
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.OpenCheckoutInBrowserCommand.Execute(null);

        Assert.Equal("https://checkout.creem.test/session", openedUrl);
        Assert.Equal(1, subscription.CreateCheckoutCallCount);
    }

    [Fact]
    public async Task SelectCardAsync_WhenEmbeddedPresenterRequestsBrowser_OpensBrowser()
    {
        var presenter = new RecordingCardCheckoutPresenter
        {
            Result = CardCheckoutPresentationResult.OpenBrowserRequested
        };
        var viewModel = CreateViewModel(CheckoutSubscription(), presenter: presenter);
        string? openedUrl = null;
        viewModel.OpenUrl = url =>
        {
            openedUrl = url;
            return true;
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Equal("https://checkout.creem.test/session", openedUrl);
    }

    [Fact]
    public async Task SelectCardAsync_WhenEmbeddedPresenterUnavailable_KeepsCheckoutPending()
    {
        var presenter = new RecordingCardCheckoutPresenter
        {
            Result = CardCheckoutPresentationResult.Unavailable
        };
        var viewModel = CreateViewModel(CheckoutSubscription(), presenter: presenter);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCardPaymentPending);
        Assert.False(viewModel.IsCardPaymentFailed);
        Assert.Contains("Open in Browser", viewModel.CardPaymentStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenEmbeddedCheckoutCommand_ReusesExistingSession()
    {
        var presenter = new RecordingCardCheckoutPresenter();
        var subscription = CheckoutSubscription();
        var viewModel = CreateViewModel(subscription, presenter: presenter);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        await viewModel.OpenEmbeddedCheckoutCommand.ExecuteAsync(null);

        Assert.Equal(2, presenter.ShowCount);
        Assert.Equal(1, subscription.CreateCheckoutCallCount);
    }

    [Fact]
    public async Task GoBackCommand_CancelsActiveEmbeddedCheckout()
    {
        var presenter = new RecordingCardCheckoutPresenter();
        var viewModel = CreateViewModel(CheckoutSubscription(), presenter: presenter);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.GoBackCommand.Execute(null);

        Assert.True(presenter.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SelectCardAsync_WhenCheckoutUrlMissing_ShowsError()
    {
        var subscription = new RecordingSubscriptionService
        {
            Checkout = new CreemCheckoutResponse
            {
                TransactionId = "ch_test_123"
            }
        };
        var viewModel = CreateViewModel(subscription);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCardPaymentFailed);
        Assert.False(viewModel.IsCardPaymentPending);
        Assert.Contains("checkout URL", viewModel.CardPaymentStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PAYMENT_PROVIDER_DISABLED", "temporarily unavailable")]
    [InlineData("ALREADY_PRO", "already has an active Pro")]
    public async Task SelectCardAsync_WhenBackendReturnsCheckoutError_ShowsSpecificMessage(string errorCode, string expectedMessage)
    {
        var subscription = new RecordingSubscriptionService
        {
            Checkout = new CreemCheckoutResponse
            {
                ErrorCode = errorCode,
                Message = "Backend message"
            }
        };
        var viewModel = CreateViewModel(subscription);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCardPaymentFailed);
        Assert.False(viewModel.IsCardPaymentPending);
        Assert.Contains(expectedMessage, viewModel.CardPaymentStatusMessage, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData("Paid")]
    [InlineData("Trialing")]
    public async Task CheckCardPaymentStatusAsync_WhenPaymentIsSuccessful_VerifiesAndRefreshesPlan(string status)
    {
        var subscription = new RecordingSubscriptionService
        {
            Checkout = new CreemCheckoutResponse
            {
                CheckoutUrl = "https://checkout.creem.test/session",
                TransactionId = "ch_test_123"
            },
            Status = new CreemPaymentStatusResponse
            {
                TransactionId = "ch_test_123",
                Status = status
            },
            Verification = new CreemPaymentVerifyResponse
            {
                Success = true,
                Status = status,
                Subscription = new CreemVerifiedSubscription { IsPro = true, BillingCycle = "Monthly" }
            }
        };
        var accountPlan = new RecordingAccountPlanService();
        var viewModel = CreateViewModel(subscription, accountPlan);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        await viewModel.CheckCardPaymentStatusCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsPaymentComplete);
        Assert.False(viewModel.IsCardPaymentPending);
        Assert.Equal(1, subscription.VerifyCallCount);
        Assert.Equal("ch_test_123", subscription.LastVerifiedTransactionId);
        Assert.Equal(1, accountPlan.RefreshCount);
        Assert.True(accountPlan.LastForce);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Canceled")]
    public async Task CheckCardPaymentStatusAsync_WhenTerminalFailure_ShowsFailure(string status)
    {
        var subscription = new RecordingSubscriptionService
        {
            Checkout = new CreemCheckoutResponse
            {
                CheckoutUrl = "https://checkout.creem.test/session",
                TransactionId = "ch_test_123"
            },
            Status = new CreemPaymentStatusResponse
            {
                TransactionId = "ch_test_123",
                Status = status
            }
        };
        var viewModel = CreateViewModel(subscription);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        await viewModel.CheckCardPaymentStatusCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCardPaymentFailed);
        Assert.False(viewModel.IsCardPaymentPending);
        Assert.Contains(status.ToLowerInvariant(), viewModel.CardPaymentStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchPaymentMethodCommand_ClearsCardState()
    {
        var subscription = new RecordingSubscriptionService
        {
            Checkout = new CreemCheckoutResponse
            {
                CheckoutUrl = "https://checkout.creem.test/session",
                TransactionId = "ch_test_123"
            }
        };
        var viewModel = CreateViewModel(subscription);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.SwitchPaymentMethodCommand.Execute(null);

        Assert.False(viewModel.IsCardSelected);
        Assert.Null(viewModel.CardCheckoutUrl);
        Assert.Null(viewModel.CardTransactionId);
        Assert.Equal(string.Empty, viewModel.CardPaymentStatus);
        Assert.Equal(string.Empty, viewModel.CardPaymentStatusMessage);
    }

    [Fact]
    public async Task SessionChanged_WhenAccountSignsOut_ClearsPaymentStateAndCancelsCheckout()
    {
        var auth = new RecordingAuthenticationService("user-a");
        var presenter = new RecordingCardCheckoutPresenter();
        var viewModel = CreateViewModel(CheckoutSubscription(), presenter: presenter, auth: auth);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        auth.SetSession(null);

        Assert.False(viewModel.IsCardSelected);
        Assert.False(viewModel.IsCardPaymentPending);
        Assert.Null(viewModel.CardTransactionId);
        Assert.Null(viewModel.CardCheckoutUrl);
        Assert.True(presenter.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SessionChanged_WhenSameAccountRefreshes_PreservesCheckout()
    {
        var auth = new RecordingAuthenticationService("user-a");
        var viewModel = CreateViewModel(CheckoutSubscription(), auth: auth);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        auth.SetSession("user-a");

        Assert.True(viewModel.IsCardPaymentPending);
        Assert.Equal("ch_test_123", viewModel.CardTransactionId);
    }

    [Fact]
    public async Task SelectCardAsync_WhenAccountChangesBeforeResponse_IgnoresOldCheckout()
    {
        var auth = new RecordingAuthenticationService("user-a");
        var subscription = new RecordingSubscriptionService { CheckoutCompletion = new TaskCompletionSource<CreemCheckoutResponse?>() };
        var viewModel = CreateViewModel(subscription, auth: auth);

        var selecting = viewModel.SelectCardCommand.ExecuteAsync(null);
        auth.SetSession("user-b");
        subscription.CheckoutCompletion.SetResult(new CreemCheckoutResponse
        {
            CheckoutUrl = "https://checkout.creem.test/session",
            TransactionId = "ch_user_a"
        });
        await selecting;

        Assert.False(viewModel.IsCardPaymentPending);
        Assert.Null(viewModel.CardTransactionId);
        Assert.Null(viewModel.CardCheckoutUrl);
    }

    [Fact]
    public async Task SessionChanged_WhenAccountChanges_LoadsOnlyNewAccountsInvoice()
    {
        var auth = new RecordingAuthenticationService("user-a");
        var subscription = new RecordingSubscriptionService();
        var viewModel = CreateViewModel(subscription, auth: auth);
        subscription.LatestInvoice = new MoneroInvoiceResponse
        {
            InvoiceId = "invoice-user-b",
            CreatedAt = DateTime.UtcNow,
            PaymentAddress = "b-address"
        };

        auth.SetSession("user-b");
        await Task.Yield();

        Assert.Equal("invoice-user-b", viewModel.MoneroInvoice?.InvoiceId);
        Assert.Equal("b-address", viewModel.MoneroInvoice?.PaymentAddress);
    }

    [Fact]
    public async Task CheckCardPaymentStatusAsync_AfterAccountSwitch_DoesNotCheckOldTransaction()
    {
        var auth = new RecordingAuthenticationService("user-a");
        var subscription = CheckoutSubscription();
        var viewModel = CreateViewModel(subscription, auth: auth);

        await viewModel.SelectCardCommand.ExecuteAsync(null);
        auth.SetSession("user-b");
        await viewModel.CheckCardPaymentStatusCommand.ExecuteAsync(null);

        Assert.Equal(0, subscription.StatusCallCount);
        Assert.Equal(0, subscription.VerifyCallCount);
    }

    private static UpgradeViewModel CreateViewModel(
        RecordingSubscriptionService subscription,
        RecordingAccountPlanService? accountPlan = null,
        RecordingCardCheckoutPresenter? presenter = null,
        RecordingAuthenticationService? auth = null)
    {
        var viewModel = new UpgradeViewModel(
            subscription,
            accountPlan ?? new RecordingAccountPlanService(),
            new RecordingNavigationService(),
            presenter ?? new RecordingCardCheckoutPresenter(),
            auth ?? new RecordingAuthenticationService("user-a"))
        {
            CardPaymentMaxPollAttempts = 0,
            CardPaymentPollInterval = TimeSpan.FromMilliseconds(1)
        };

        viewModel.OpenUrl = _ => true;
        return viewModel;
    }

    private static RecordingSubscriptionService CheckoutSubscription() => new()
    {
        Checkout = new CreemCheckoutResponse
        {
            CheckoutUrl = "https://checkout.creem.test/session",
            TransactionId = "ch_test_123"
        }
    };

    private sealed class RecordingSubscriptionService : ISubscriptionService
    {
        public CreemCheckoutResponse? Checkout { get; set; }
        public CreemPaymentStatusResponse? Status { get; set; }
        public CreemPaymentVerifyResponse? Verification { get; set; }
        public int VerifyCallCount { get; private set; }
        public int CreateCheckoutCallCount { get; private set; }
        public int StatusCallCount { get; private set; }
        public string? LastVerifiedTransactionId { get; private set; }
        public TaskCompletionSource<CreemCheckoutResponse?>? CheckoutCompletion { get; set; }
        public MoneroInvoiceResponse? LatestInvoice { get; set; }

        public Task<SubscriptionStatusResponse?> GetStatusAsync(CancellationToken ct = default) => Task.FromResult<SubscriptionStatusResponse?>(null);
        public Task<DataQuotaResponse?> GetQuotaAsync(CancellationToken ct = default) => Task.FromResult<DataQuotaResponse?>(null);
        public Task<CanConnectResponse?> CanConnectAsync(CancellationToken ct = default) =>
            Task.FromResult<CanConnectResponse?>(new CanConnectResponse { Allowed = true });
        public Task<bool> ValidateTokenAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MoneroPriceResponse?> GetMoneroPriceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) => Task.FromResult<MoneroPriceResponse?>(null);
        public Task<MoneroInvoiceResponse?> CreateMoneroInvoiceAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default) => Task.FromResult<MoneroInvoiceResponse?>(null);
        public Task<MoneroStatusResponse?> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken ct = default) => Task.FromResult<MoneroStatusResponse?>(null);
        public Task<MoneroInvoiceResponse?> GetLatestMoneroInvoiceAsync(CancellationToken ct = default) => Task.FromResult(LatestInvoice);
        public Task<CreemCheckoutResponse?> CreateCreemCheckoutAsync(BillingCycle cycle = BillingCycle.Monthly, CancellationToken ct = default)
        {
            CreateCheckoutCallCount++;
            return CheckoutCompletion?.Task ?? Task.FromResult(Checkout);
        }
        public Task<CreemPaymentStatusResponse?> GetCreemPaymentStatusAsync(string transactionId, CancellationToken ct = default)
        {
            StatusCallCount++;
            return Task.FromResult(Status);
        }
        public Task<CreemPaymentVerifyResponse?> VerifyCreemPaymentAsync(string transactionId, CancellationToken ct = default)
        {
            VerifyCallCount++;
            LastVerifiedTransactionId = transactionId;
            return Task.FromResult(Verification);
        }
    }

    private sealed class RecordingCardCheckoutPresenter : ICardCheckoutPresenter
    {
        public CardCheckoutPresentationResult Result { get; set; } = CardCheckoutPresentationResult.Closed;
        public int ShowCount { get; private set; }
        public Uri? LastUri { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<CardCheckoutPresentationResult> ShowAsync(Uri checkoutUri, CancellationToken cancellationToken = default)
        {
            ShowCount++;
            LastUri = checkoutUri;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingAccountPlanService : IAccountPlanService
    {
        public UserPlan CurrentPlan { get; private set; } = UserPlan.Free;
        public bool IsPro => CurrentPlan == UserPlan.Pro;
        public bool IsOpenVpnAvailable => IsPro;
        public string CurrentPlanLabel => IsPro ? "Pro" : "Free";
        public bool IsRefreshing => false;
        public int RefreshCount { get; private set; }
        public bool LastForce { get; private set; }
        public event Action? PlanChanged;

        public Task RefreshAsync(bool force = false, CancellationToken ct = default)
        {
            RefreshCount++;
            LastForce = force;
            CurrentPlan = UserPlan.Pro;
            PlanChanged?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNavigationService : INavigationService
    {
        public string CurrentView { get; private set; } = "upgrade";
        public event EventHandler<string>? ViewChanged;

        public void NavigateTo(string viewKey)
        {
            CurrentView = viewKey;
            ViewChanged?.Invoke(this, viewKey);
        }
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public RecordingAuthenticationService(string? userId) => SetSession(userId, raiseEvent: false);

        public event Action? SessionChanged;
        public bool IsAuthenticated { get; private set; }
        public string? UserEmail => IsAuthenticated ? $"{UserId}@example.test" : null;
        public string? UserId { get; private set; }
        public UserPlan Plan => UserPlan.Free;

        public void SetSession(string? userId, bool raiseEvent = true)
        {
            UserId = userId;
            IsAuthenticated = !string.IsNullOrWhiteSpace(userId);
            if (raiseEvent) SessionChanged?.Invoke();
        }

        public Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> Verify2FaAsync(string email, string code, string? pendingLoginToken = null, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithGoogleAsync(GoogleLoginContext loginContext, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithOAuthAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> LoginWithTokenAsync(string token, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default) => Task.FromResult(AuthResult.Ok());
        public Task<bool> CheckEmailConfirmationAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResendConfirmationAsync(string email, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthAsync(string email, string password, int deviceIdToRemove, CancellationToken cancellationToken = default) => Task.FromResult(PreAuthDeviceRemovalResult.Fail("Not supported."));
        public Task<PreAuthDeviceRemovalResult> RemoveDevicePreAuthOAuthAsync(GoogleLoginContext loginContext, string provider, int deviceIdToRemove, CancellationToken cancellationToken = default) => Task.FromResult(PreAuthDeviceRemovalResult.Fail("Not supported."));
        public Task<TwoFactorSetupResponse?> InitiateTwoFactorSetupAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorSetupResponse?>(null);
        public Task<TwoFactorEnableResponse?> VerifyAndEnableTwoFactorAsync(string code, CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorEnableResponse?>(null);
        public Task<TwoFactorRecoveryCodesResponse?> GenerateRecoveryCodesAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorRecoveryCodesResponse?>(null);
        public Task<TwoFactorDisableResponse?> DisableTwoFactorAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorDisableResponse?>(null);
        public Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PasswordResetResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default) => Task.FromResult(PasswordResetResult.Ok("ok"));
        public Task<TwoFactorStatusResponse?> GetTwoFactorStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<TwoFactorStatusResponse?>(null);
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
