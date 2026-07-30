using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

public interface IAccountPlanService
{
    UserPlan CurrentPlan { get; }
    bool IsPro { get; }
    bool IsOpenVpnAvailable { get; }
    string CurrentPlanLabel { get; }
    bool IsRefreshing { get; }
    event Action? PlanChanged;
    Task RefreshAsync(bool force = false, CancellationToken ct = default);
}

/// <summary>
/// Central source of truth for account plan state across the desktop app.
/// </summary>
internal sealed class AccountPlanService : IAccountPlanService
{
    private readonly IAuthenticationService _authService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private UserPlan _currentPlan;
    private string _currentPlanLabel;
    private bool _isRefreshing;

    public AccountPlanService(IAuthenticationService authService, ISubscriptionService subscriptionService)
    {
        _authService = authService;
        _subscriptionService = subscriptionService;
        _currentPlan = _authService.Plan;
        _currentPlanLabel = _currentPlan == UserPlan.Pro ? "Pro" : "Free";

        _authService.SessionChanged += SyncCachedPlan;
    }

    public UserPlan CurrentPlan => _currentPlan;
    public bool IsPro => _currentPlan == UserPlan.Pro;
    public bool IsOpenVpnAvailable => IsPro;
    public string CurrentPlanLabel => _currentPlanLabel;
    public bool IsRefreshing => _isRefreshing;
    public event Action? PlanChanged;

    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        SyncCachedPlan();

        if (!_authService.IsAuthenticated)
            return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            SetRefreshing(true);

            var status = await GetStatusAsync(force, ct);
            if (status is null)
                return;

            ApplyStatus(status);
        }
        finally
        {
            SetRefreshing(false);
            _refreshLock.Release();
        }
    }

    private void SyncCachedPlan()
    {
        if (!_authService.IsAuthenticated)
        {
            ApplyPlan(UserPlan.Free, "Free");
            return;
        }

        var cachedPlan = _authService.Plan;
        var label = cachedPlan == UserPlan.Pro ? "Pro" : "Free";
        ApplyPlan(cachedPlan, label);
    }

    private async Task<SubscriptionStatusResponse?> GetStatusAsync(bool force, CancellationToken ct)
    {
        var status = await _subscriptionService.GetStatusAsync(ct);
        if (status is not null || !force)
            return status;

        for (var attempt = 0; attempt < 3 && status is null; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct);
            status = await _subscriptionService.GetStatusAsync(ct);
        }

        return status;
    }

    private void ApplyStatus(SubscriptionStatusResponse status)
    {
        var plan = status.IsPro ? UserPlan.Pro : UserPlan.Free;
        var label = status.IsPro
            ? string.IsNullOrWhiteSpace(status.BillingCycle) ? "Pro" : $"Pro ({status.BillingCycle})"
            : "Free";

        ApplyPlan(plan, label);
    }

    private void ApplyPlan(UserPlan plan, string label)
    {
        if (_currentPlan == plan && string.Equals(_currentPlanLabel, label, StringComparison.Ordinal))
            return;

        _currentPlan = plan;
        _currentPlanLabel = label;
        PlanChanged?.Invoke();
    }

    private void SetRefreshing(bool value)
    {
        if (_isRefreshing == value)
            return;

        _isRefreshing = value;
        PlanChanged?.Invoke();
    }
}
