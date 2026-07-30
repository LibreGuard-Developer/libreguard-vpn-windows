using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.ViewModels;

/// <summary>
/// Manages upgrade payments for the currently authenticated account only.
/// </summary>
public sealed partial class UpgradeViewModel : ObservableObject
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAccountPlanService _accountPlanService;
    private readonly INavigationService _navigationService;
    private readonly ICardCheckoutPresenter _cardCheckoutPresenter;
    private readonly IAuthenticationService _authService;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _cardPaymentCts;
    private string? _activeUserId;
    private long _sessionGeneration;

    internal Func<string, bool> OpenUrl { get; set; } = OpenUrlInDefaultBrowser;
    internal TimeSpan CardPaymentPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal int CardPaymentMaxPollAttempts { get; set; } = 72;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isMoneroSelected;
    [ObservableProperty] private bool _isCardSelected;
    [ObservableProperty] private bool _isPaymentMethodSelectionVisible = true;
    [ObservableProperty] private MoneroPriceResponse? _moneroPrice;
    [ObservableProperty] private MoneroInvoiceResponse? _moneroInvoice;
    [ObservableProperty] private MoneroStatusResponse? _moneroStatus;
    [ObservableProperty] private decimal _shortfall;
    [ObservableProperty] private string _timeRemaining = string.Empty;
    [ObservableProperty] private bool _isPaymentComplete;
    [ObservableProperty] private string? _cardCheckoutUrl;
    [ObservableProperty] private string? _cardTransactionId;
    [ObservableProperty] private string _cardPaymentStatus = string.Empty;
    [ObservableProperty] private string _cardPaymentStatusMessage = string.Empty;
    [ObservableProperty] private bool _isCardPaymentPending;
    [ObservableProperty] private bool _isCardPaymentFailed;
    [ObservableProperty] private BillingCycle _selectedCycle = BillingCycle.Monthly;

    public UpgradeViewModel(
        ISubscriptionService subscriptionService,
        IAccountPlanService accountPlanService,
        INavigationService navigationService,
        ICardCheckoutPresenter cardCheckoutPresenter,
        IAuthenticationService authService)
    {
        _subscriptionService = subscriptionService;
        _accountPlanService = accountPlanService;
        _navigationService = navigationService;
        _cardCheckoutPresenter = cardCheckoutPresenter;
        _authService = authService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _activeUserId = GetAuthenticatedUserId();

        _authService.SessionChanged += OnSessionChanged;
        if (_activeUserId is not null)
            _ = InitializeAsync(CaptureScope());
    }

    partial void OnSelectedCycleChanged(BillingCycle value) => _ = UpdatePriceAsync(CaptureScope());
    partial void OnIsMoneroSelectedChanged(bool value) => UpdatePaymentMethodSelectionVisibility();
    partial void OnIsCardSelectedChanged(bool value) => UpdatePaymentMethodSelectionVisibility();

    private void UpdatePaymentMethodSelectionVisibility() =>
        IsPaymentMethodSelectionVisible = !IsMoneroSelected && !IsCardSelected;

    private void OnSessionChanged()
    {
        if (_dispatcher.CheckAccess())
            HandleSessionChanged();
        else
            _ = _dispatcher.BeginInvoke(new Action(HandleSessionChanged));
    }

    private void HandleSessionChanged()
    {
        var currentUserId = GetAuthenticatedUserId();
        if (string.Equals(_activeUserId, currentUserId, StringComparison.Ordinal))
            return;

        ++_sessionGeneration;
        CancelPaymentOperations();
        ClearPaymentState();
        _activeUserId = currentUserId;

        if (currentUserId is not null)
            _ = InitializeAsync(CaptureScope());
    }

    private async Task InitializeAsync(SessionScope? scope)
    {
        if (scope is null) return;

        try
        {
            var latest = await _subscriptionService.GetLatestMoneroInvoiceAsync();
            if (!IsCurrent(scope) || latest is null || latest.CreatedAt.AddHours(24) <= DateTime.UtcNow)
                return;

            if (latest.BillingCycle is not null && Enum.TryParse<BillingCycle>(latest.BillingCycle, true, out var cycle))
                SelectedCycle = cycle;

            MoneroInvoice = latest;
            IsMoneroSelected = true;
            MoneroPrice = await _subscriptionService.GetMoneroPriceAsync(SelectedCycle);
            if (!IsCurrent(scope)) return;

            await CheckPaymentStatusAsync(scope, latest);
            if (IsCurrent(scope) && MoneroInvoice == latest)
                StartTimer();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task UpdatePriceAsync(SessionScope? scope)
    {
        if (scope is null) return;
        try
        {
            var price = await _subscriptionService.GetMoneroPriceAsync(SelectedCycle);
            if (IsCurrent(scope)) MoneroPrice = price;
        }
        catch { }
    }

    [RelayCommand]
    private async Task SelectMoneroAsync()
    {
        var scope = CaptureScope();
        if (scope is null) return;

        IsMoneroSelected = true;
        IsCardSelected = false;
        IsLoading = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var price = await _subscriptionService.GetMoneroPriceAsync(SelectedCycle, _cts.Token);
            if (!IsCurrent(scope)) return;
            MoneroPrice = price;

            var invoice = await _subscriptionService.CreateMoneroInvoiceAsync(SelectedCycle, _cts.Token);
            if (!IsCurrent(scope)) return;
            MoneroInvoice = invoice;

            if (invoice is not null)
            {
                await CheckPaymentStatusAsync(scope, invoice);
                if (IsCurrent(scope) && MoneroInvoice == invoice)
                    StartTimer();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (IsCurrent(scope)) IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectCardAsync()
    {
        var scope = CaptureScope();
        if (scope is null) return;

        CancelCardPaymentPolling();
        IsCardSelected = true;
        IsMoneroSelected = false;
        IsLoading = true;
        IsPaymentComplete = false;
        IsCardPaymentPending = false;
        IsCardPaymentFailed = false;
        CardCheckoutUrl = null;
        CardTransactionId = null;
        CardPaymentStatus = "Creating checkout";
        CardPaymentStatusMessage = "Creating secure card checkout...";

        try
        {
            var checkout = await _subscriptionService.CreateCreemCheckoutAsync(SelectedCycle);
            if (!IsCurrent(scope)) return;
            if (checkout is null)
            {
                MarkCardPaymentFailed("Card checkout is unavailable right now. Please try again shortly.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(checkout.ErrorCode))
            {
                MarkCardPaymentFailed(GetCardCheckoutErrorMessage(checkout));
                return;
            }
            if (string.IsNullOrWhiteSpace(checkout.CheckoutUrl))
            {
                MarkCardPaymentFailed("Card checkout did not return a checkout URL. Please try again.");
                return;
            }
            if (string.IsNullOrWhiteSpace(checkout.TransactionId))
            {
                MarkCardPaymentFailed("Card checkout did not return a transaction ID. Please try again.");
                return;
            }

            CardCheckoutUrl = checkout.CheckoutUrl;
            CardTransactionId = checkout.TransactionId;
            IsCardPaymentPending = true;
            CardPaymentStatus = "Waiting for payment";
            CardPaymentStatusMessage = "Complete payment securely inside LibreGuard. This screen will update when payment is confirmed.";
            _cardPaymentCts = new CancellationTokenSource();
            _ = PollCardPaymentAsync(scope.Value, checkout.TransactionId, _cardPaymentCts.Token);
            await PresentEmbeddedCheckoutAsync(scope.Value, checkout.CheckoutUrl, _cardPaymentCts.Token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (IsCurrent(scope)) MarkCardPaymentFailed("Could not create card checkout. Please try again.");
        }
        finally
        {
            if (IsCurrent(scope)) IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task OpenEmbeddedCheckoutAsync()
    {
        var scope = CaptureScope();
        if (scope is null || string.IsNullOrWhiteSpace(CardCheckoutUrl)) return;
        if (_cardPaymentCts is null || _cardPaymentCts.IsCancellationRequested)
        {
            _cardPaymentCts?.Dispose();
            _cardPaymentCts = new CancellationTokenSource();
        }
        await PresentEmbeddedCheckoutAsync(scope.Value, CardCheckoutUrl, _cardPaymentCts.Token);
    }

    [RelayCommand]
    private void OpenCheckoutInBrowser()
    {
        if (CaptureScope() is null) return;
        if (!OpenCheckoutUrlInBrowser())
            CardPaymentStatusMessage = "Could not open checkout in your browser. Please try again.";
    }

    [RelayCommand]
    private async Task CheckCardPaymentStatusAsync()
    {
        var scope = CaptureScope();
        var transactionId = CardTransactionId;
        if (scope is null || string.IsNullOrWhiteSpace(transactionId)) return;
        IsLoading = true;
        try { await RefreshCardPaymentStatusAsync(scope.Value, transactionId, CancellationToken.None); }
        finally { if (IsCurrent(scope)) IsLoading = false; }
    }

    [RelayCommand]
    private void CopyAddress()
    {
        if (!string.IsNullOrEmpty(MoneroInvoice?.PaymentAddress)) Clipboard.SetText(MoneroInvoice.PaymentAddress);
    }

    [RelayCommand]
    private void CopyAmount()
    {
        if (MoneroPrice is not null) Clipboard.SetText(MoneroPrice.XmrAmount.ToString());
    }

    [RelayCommand]
    private async Task CheckPaymentStatusAsync() => await CheckPaymentStatusAsync(CaptureScope(), MoneroInvoice);

    private async Task CheckPaymentStatusAsync(SessionScope? scope, MoneroInvoiceResponse? invoice)
    {
        if (scope is null || !IsCurrent(scope) || invoice is null || string.IsNullOrWhiteSpace(invoice.InvoiceId)) return;
        IsLoading = true;
        try
        {
            var status = await _subscriptionService.GetMoneroPaymentStatusAsync(invoice.InvoiceId);
            if (!IsCurrent(scope) || MoneroInvoice != invoice) return;
            MoneroStatus = status;
            if (status is null) return;
            Shortfall = Math.Max(0, status.AmountRequired - status.AmountReceived);
            if (status.Confirmations >= status.RequiredConfirmations)
            {
                IsPaymentComplete = true;
                await _accountPlanService.RefreshAsync(force: true);
                if (IsCurrent(scope) && MoneroInvoice == invoice) StopTimer();
            }
        }
        finally { if (IsCurrent(scope)) IsLoading = false; }
    }

    [RelayCommand]
    private void GoBack()
    {
        CancelCardPaymentPolling();
        StopTimer();
        _navigationService.NavigateTo("settings");
    }

    [RelayCommand]
    private void SwitchPaymentMethod()
    {
        CancelCardPaymentPolling();
        ClearPaymentState();
    }

    private async Task PollCardPaymentAsync(SessionScope scope, string transactionId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CardPaymentMaxPollAttempts; attempt++)
        {
            try
            {
                await Task.Delay(CardPaymentPollInterval, cancellationToken);
                if (!IsCurrent(scope) || await RefreshCardPaymentStatusAsync(scope, transactionId, cancellationToken)) return;
            }
            catch (OperationCanceledException) { return; }
        }

        if (IsCurrent(scope) && !cancellationToken.IsCancellationRequested && IsCardPaymentPending && CardTransactionId == transactionId)
        {
            CardPaymentStatus = "Still pending";
            CardPaymentStatusMessage = "Payment is still pending. Use Check Status after completing checkout.";
        }
    }

    private async Task<bool> RefreshCardPaymentStatusAsync(SessionScope scope, string transactionId, CancellationToken cancellationToken)
    {
        if (!IsCurrent(scope) || CardTransactionId != transactionId) return true;
        var status = await _subscriptionService.GetCreemPaymentStatusAsync(transactionId, cancellationToken);
        if (!IsCurrent(scope) || CardTransactionId != transactionId) return true;
        if (status is null)
        {
            CardPaymentStatus = "Pending";
            CardPaymentStatusMessage = "Payment is not confirmed yet. Complete checkout, then check again.";
            return false;
        }

        CardPaymentStatus = status.Status ?? "Pending";
        if (IsPaidStatus(status.Status)) return await VerifyCompletedCardPaymentAsync(scope, transactionId, cancellationToken);
        if (IsFailedStatus(status.Status))
        {
            MarkCardPaymentFailed($"Card payment {status.Status?.ToLowerInvariant() ?? "failed"}. Please try again.");
            return true;
        }
        IsCardPaymentPending = true;
        IsCardPaymentFailed = false;
        CardPaymentStatusMessage = "Payment is not confirmed yet. Complete checkout, then check again.";
        return false;
    }

    private async Task<bool> VerifyCompletedCardPaymentAsync(SessionScope scope, string transactionId, CancellationToken cancellationToken)
    {
        if (!IsCurrent(scope) || CardTransactionId != transactionId) return true;
        var verification = await _subscriptionService.VerifyCreemPaymentAsync(transactionId, cancellationToken);
        if (!IsCurrent(scope) || CardTransactionId != transactionId) return true;
        if (verification?.Success == true
            || verification?.Subscription?.IsPro == true
            || IsPaidStatus(verification?.Status))
        {
            IsPaymentComplete = true;
            IsCardPaymentPending = false;
            IsCardPaymentFailed = false;
            CardPaymentStatus = "Paid";
            CardPaymentStatusMessage = "Payment confirmed. Your Pro account is active.";
            await _accountPlanService.RefreshAsync(force: true, cancellationToken);
            if (IsCurrent(scope) && CardTransactionId == transactionId) CancelCardPaymentPolling();
            return true;
        }

        IsCardPaymentPending = true;
        IsCardPaymentFailed = false;
        CardPaymentStatus = verification?.Status ?? "Paid";
        CardPaymentStatusMessage = "Payment was received. Waiting for subscription activation.";
        await _accountPlanService.RefreshAsync(force: true, cancellationToken);
        return false;
    }

    private async Task PresentEmbeddedCheckoutAsync(SessionScope scope, string checkoutUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(checkoutUrl, UriKind.Absolute, out var checkoutUri) || !CardCheckoutNavigationPolicy.IsAllowedWebUri(checkoutUri))
        {
            if (IsCurrent(scope)) CardPaymentStatusMessage = "Checkout is ready, but its address is invalid. Please try again.";
            return;
        }
        try
        {
            var result = await _cardCheckoutPresenter.ShowAsync(checkoutUri, cancellationToken);
            if (!IsCurrent(scope) || CardCheckoutUrl != checkoutUrl) return;
            if (result == CardCheckoutPresentationResult.OpenBrowserRequested && !OpenCheckoutUrlInBrowser())
                CardPaymentStatusMessage = "Could not open checkout in your browser. Use Open in Browser to try again.";
            else if (result == CardCheckoutPresentationResult.Unavailable && IsCardPaymentPending)
                CardPaymentStatusMessage = "Embedded checkout is unavailable. Use Open in Browser to continue.";
            else if (result == CardCheckoutPresentationResult.ReturnDetected && IsCardPaymentPending)
            {
                CardPaymentStatus = "Confirming payment";
                CardPaymentStatusMessage = "Payment was submitted. Waiting for secure confirmation from Creem.";
            }
        }
        catch (OperationCanceledException) { }
        catch { if (IsCurrent(scope) && IsCardPaymentPending) CardPaymentStatusMessage = "Embedded checkout is unavailable. Use Open in Browser to continue."; }
    }

    private void ClearPaymentState()
    {
        IsLoading = false;
        IsMoneroSelected = false;
        IsCardSelected = false;
        MoneroPrice = null;
        MoneroInvoice = null;
        MoneroStatus = null;
        Shortfall = 0;
        TimeRemaining = string.Empty;
        CardCheckoutUrl = null;
        CardTransactionId = null;
        CardPaymentStatus = string.Empty;
        CardPaymentStatusMessage = string.Empty;
        IsCardPaymentPending = false;
        IsCardPaymentFailed = false;
        IsPaymentComplete = false;
        StopTimer();
    }

    private void CancelPaymentOperations()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        CancelCardPaymentPolling();
    }

    private bool OpenCheckoutUrlInBrowser()
    {
        if (string.IsNullOrWhiteSpace(CardCheckoutUrl)) return false;
        try { return OpenUrl(CardCheckoutUrl); }
        catch { return false; }
    }

    private void MarkCardPaymentFailed(string message)
    {
        IsCardPaymentPending = false;
        IsCardPaymentFailed = true;
        IsPaymentComplete = false;
        CardPaymentStatus = "Unable to continue";
        CardPaymentStatusMessage = message;
        CancelCardPaymentPolling();
    }

    private void CancelCardPaymentPolling()
    {
        _cardPaymentCts?.Cancel();
        _cardPaymentCts?.Dispose();
        _cardPaymentCts = null;
    }

    private SessionScope? CaptureScope() => _activeUserId is { Length: > 0 } userId ? new SessionScope(userId, _sessionGeneration) : null;
    private bool IsCurrent(SessionScope? scope) => scope is { } value && value.Generation == _sessionGeneration && string.Equals(value.UserId, _activeUserId, StringComparison.Ordinal) && string.Equals(value.UserId, GetAuthenticatedUserId(), StringComparison.Ordinal);
    private string? GetAuthenticatedUserId() => _authService.IsAuthenticated && !string.IsNullOrWhiteSpace(_authService.UserId) ? _authService.UserId : null;
    private readonly record struct SessionScope(string UserId, long Generation);

    private static bool IsPaidStatus(string? status) => string.Equals(status, "Paid", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Trialing", StringComparison.OrdinalIgnoreCase);
    private static bool IsFailedStatus(string? status) => string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Refunded", StringComparison.OrdinalIgnoreCase);
    private static string GetCardCheckoutErrorMessage(CreemCheckoutResponse checkout) => checkout.ErrorCode switch
    {
        "PAYMENT_PROVIDER_DISABLED" => "Card payments are temporarily unavailable. Please try again later or choose Monero.",
        "ALREADY_PRO" => "Your account already has an active Pro subscription.",
        "EMAIL_REQUIRED" => "A verified account email is required before card checkout.",
        "CHECKOUT_FAILED" => "Could not create checkout session. Please try again.",
        _ => string.IsNullOrWhiteSpace(checkout.Message) ? "Could not create card checkout. Please try again." : checkout.Message
    };
    private static bool OpenUrlInDefaultBrowser(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        return true;
    }

    private void StartTimer()
    {
        StopTimer();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        UpdateTimerDisplay();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateTimerDisplay();
        if (DateTime.Now.Second == 0) await CheckPaymentStatusAsync();
    }

    private void UpdateTimerDisplay()
    {
        if (MoneroInvoice is null) return;
        var remaining = MoneroInvoice.CreatedAt.AddHours(24) - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0) { TimeRemaining = "Expired"; StopTimer(); }
        else TimeRemaining = $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
    }
}
