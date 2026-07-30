using LibreGuard_VPN_Desktop.Models;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;
using LibreGuard_VPN_Desktop.ViewModels;

namespace LibreGuard_VPN_Desktop.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Constructor_LoadsThemePreferenceFromSettings()
    {
        var viewModel = CreateViewModel(new UserSettings { ThemePreference = AppThemePreference.Dark });

        Assert.Equal(AppThemePreference.Dark, viewModel.ThemePreference);
    }

    [Fact]
    public void Constructor_DefaultSettingsUseSystemThemePreference()
    {
        var viewModel = CreateViewModel(new UserSettings());

        Assert.Equal(AppThemePreference.System, viewModel.ThemePreference);
    }

    [Fact]
    public void ThemePreference_WhenChanged_AppliesAndSavesSetting()
    {
        var settingsService = new RecordingUserSettingsService(new UserSettings());
        var themeService = new RecordingThemeService();
        var viewModel = CreateViewModel(settingsService, themeService);

        viewModel.ThemePreference = AppThemePreference.Dark;

        Assert.Equal(AppThemePreference.Dark, settingsService.Settings.ThemePreference);
        Assert.Equal(AppThemePreference.Dark, themeService.LastPreference);
        Assert.Equal(1, themeService.ApplyCount);
        Assert.Equal(1, settingsService.SaveCount);
    }

    [Theory]
    [InlineData(false, true, false, "Off. LibreGuard private DNS remains active.")]
    [InlineData(false, true, true, "filtering is still being disabled")]
    [InlineData(true, true, true, "Ads and known tracking domains are filtered")]
    [InlineData(true, true, false, "Saved, temporarily unavailable")]
    [InlineData(true, false, false, "paused until Pro is restored")]
    [InlineData(false, false, false, "available with Pro")]
    public async Task Refresh_MapsAuthoritativeDnsStateToHonestPresentation(
        bool requested,
        bool canUse,
        bool effective,
        string expectedStatus)
    {
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(requested, canUse, effective, effective ? "filtered" : "regular", 15)
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService);

        await viewModel.RefreshUserDataAsync();

        Assert.True(viewModel.HasAdBlockingPreference);
        Assert.Equal(requested, viewModel.AdBlockingRequestedEnabled);
        Assert.Equal(canUse, viewModel.CanUseAdBlocking);
        Assert.Equal(effective, viewModel.AdBlockingEffectiveEnabled);
        Assert.Equal(effective ? "filtered" : "regular", viewModel.AdBlockingEffectiveMode);
        Assert.Equal(15, viewModel.AdBlockingPropagationSeconds);
        Assert.Contains(expectedStatus, viewModel.AdBlockingStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Toggle_DoesNotOptimisticallyChangeState_AndAppliesSuccessfulResponse()
    {
        var updateCompletion = new TaskCompletionSource<DnsPreferenceUpdateResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(false, true, false),
            SetHandler = (_, _) => updateCompletion.Task
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService);
        await viewModel.RefreshUserDataAsync();

        var operation = viewModel.ToggleAdBlockingCommand.ExecuteAsync(null);
        await dnsService.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsAdBlockingBusy);
        Assert.False(viewModel.AdBlockingRequestedEnabled);
        Assert.Contains("Enabling", viewModel.AdBlockingStatus, StringComparison.OrdinalIgnoreCase);

        updateCompletion.SetResult(new DnsPreferenceUpdateResult
        {
            Success = true,
            Preference = Preference(true, true, true, "filtered", 15)
        });
        await operation;

        Assert.True(dnsService.LastRequestedValue);
        Assert.True(viewModel.AdBlockingRequestedEnabled);
        Assert.True(viewModel.AdBlockingEffectiveEnabled);
        Assert.False(viewModel.IsAdBlockingBusy);
        Assert.Contains("within 15 seconds", viewModel.AdBlockingStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Toggle_FailureRetainsAuthoritativeState_AndShowsRetryStatus()
    {
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(false, true, false),
            SetHandler = (_, _) => Task.FromResult(new DnsPreferenceUpdateResult
            {
                Success = false,
                ErrorCode = "SERVER_ERROR",
                Message = "The DNS settings service is unavailable."
            })
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService);
        await viewModel.RefreshUserDataAsync();

        await viewModel.ToggleAdBlockingCommand.ExecuteAsync(null);

        Assert.False(viewModel.AdBlockingRequestedEnabled);
        Assert.False(viewModel.AdBlockingEffectiveEnabled);
        Assert.Contains("Try again", viewModel.AdBlockingStatus, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.CanToggleAdBlocking);
    }

    [Fact]
    public async Task Toggle_CancellationRetainsStateAndPriorStatus()
    {
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(false, true, false),
            SetHandler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            }
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService);
        await viewModel.RefreshUserDataAsync();
        var priorStatus = viewModel.AdBlockingStatus;

        var operation = viewModel.ToggleAdBlockingCommand.ExecuteAsync(null);
        await dnsService.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.ToggleAdBlockingCommand.Cancel();
        await operation;

        Assert.False(viewModel.AdBlockingRequestedEnabled);
        Assert.False(viewModel.AdBlockingEffectiveEnabled);
        Assert.False(viewModel.IsAdBlockingBusy);
        Assert.Equal(priorStatus, viewModel.AdBlockingStatus);
    }

    [Fact]
    public async Task Toggle_TokenRotationDuringFailedSave_DoesNotClearOrReloadPreference()
    {
        var updateCompletion = new TaskCompletionSource<DnsPreferenceUpdateResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authenticationService = new StaticAuthenticationService();
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(false, true, false),
            SetHandler = (_, _) => updateCompletion.Task
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService,
            authenticationService);
        await viewModel.RefreshUserDataAsync();
        var getCountBeforeSave = dnsService.GetCount;

        var operation = viewModel.ToggleAdBlockingCommand.ExecuteAsync(null);
        await dnsService.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        authenticationService.RaiseSessionChanged();

        Assert.True(viewModel.HasAdBlockingPreference);
        Assert.False(viewModel.AdBlockingRequestedEnabled);
        Assert.True(viewModel.IsAdBlockingBusy);
        Assert.Equal(getCountBeforeSave, dnsService.GetCount);

        updateCompletion.SetResult(new DnsPreferenceUpdateResult
        {
            Success = false,
            ErrorCode = "SERVER_ERROR",
            Message = "Temporarily unavailable."
        });
        await operation;

        Assert.False(viewModel.AdBlockingRequestedEnabled);
        Assert.True(viewModel.HasAdBlockingPreference);
        Assert.Contains("Try again", viewModel.AdBlockingStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Toggle_RepeatedExecutionWhileSaving_SendsOneUpdate()
    {
        var updateCompletion = new TaskCompletionSource<DnsPreferenceUpdateResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(false, true, false),
            SetHandler = (_, _) => updateCompletion.Task
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService);
        await viewModel.RefreshUserDataAsync();

        var first = viewModel.ToggleAdBlockingCommand.ExecuteAsync(null);
        await dnsService.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = viewModel.ToggleAdBlockingCommand.ExecuteAsync(null);

        Assert.Equal(1, dnsService.SetCount);

        updateCompletion.SetResult(new DnsPreferenceUpdateResult
        {
            Success = true,
            Preference = Preference(true, true, true, "filtered", 15)
        });
        await Task.WhenAll(first, second);

        Assert.Equal(1, dnsService.SetCount);
        Assert.True(viewModel.AdBlockingRequestedEnabled);
    }

    [Fact]
    public async Task Toggle_ResultFromPriorSession_DoesNotApplyAfterSameAccountRelogin()
    {
        var updateCompletion = new TaskCompletionSource<DnsPreferenceUpdateResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authenticationService = new StaticAuthenticationService();
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(false, true, false),
            SetHandler = (_, _) => updateCompletion.Task
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService,
            authenticationService);
        await viewModel.RefreshUserDataAsync();

        var oldSessionOperation = viewModel.ToggleAdBlockingCommand.ExecuteAsync(null);
        await dnsService.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        authenticationService.SetAuthenticated(false);
        authenticationService.SetAuthenticated(true);
        var newSessionRefresh = viewModel.RefreshUserDataAsync();

        updateCompletion.SetResult(new DnsPreferenceUpdateResult
        {
            Success = true,
            Preference = Preference(true, true, true, "filtered", 15)
        });

        await oldSessionOperation;
        await newSessionRefresh;

        Assert.True(viewModel.HasAdBlockingPreference);
        Assert.False(viewModel.AdBlockingRequestedEnabled);
        Assert.False(viewModel.AdBlockingEffectiveEnabled);
    }

    [Fact]
    public async Task Toggle_ProRequiredRetainsStateAndLocksCardToUpgradePath()
    {
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(false, true, false),
            SetHandler = (_, _) => Task.FromResult(new DnsPreferenceUpdateResult
            {
                Success = false,
                ErrorCode = "PRO_REQUIRED",
                Message = "Upgrade to Pro."
            })
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService);
        await viewModel.RefreshUserDataAsync();

        await viewModel.ToggleAdBlockingCommand.ExecuteAsync(null);

        Assert.False(viewModel.AdBlockingRequestedEnabled);
        Assert.True(viewModel.IsAdBlockingUpgradeRequired);
        Assert.False(viewModel.CanToggleAdBlocking);
        Assert.Contains("requires Pro", viewModel.AdBlockingStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Downgrade_PreservesRequestedPreference_ThenUsesDnsEntitlementAuthority()
    {
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(true, true, true)
        };
        var planService = new StaticAccountPlanService(UserPlan.Pro);
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService,
            accountPlanService: planService);
        await viewModel.RefreshUserDataAsync();

        planService.SetPlan(UserPlan.Free);

        Assert.True(viewModel.AdBlockingRequestedEnabled);
        Assert.False(viewModel.CanToggleAdBlocking);
        Assert.True(viewModel.IsAdBlockingUpgradeRequired);
        Assert.Contains("paused", viewModel.AdBlockingStatus, StringComparison.OrdinalIgnoreCase);

        dnsService.Preference = Preference(true, false, false, "regular", 15);
        await viewModel.RefreshUserDataAsync();

        Assert.True(viewModel.AdBlockingRequestedEnabled);
        Assert.False(viewModel.CanUseAdBlocking);
        Assert.False(viewModel.AdBlockingEffectiveEnabled);
        Assert.True(viewModel.IsAdBlockingUpgradeRequired);
        Assert.Contains("paused", viewModel.AdBlockingStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_ClearsAllVisibleDnsPreferenceState()
    {
        var authenticationService = new StaticAuthenticationService();
        var dnsService = new RecordingDnsSettingsService
        {
            Preference = Preference(true, true, true, "filtered", 15)
        };
        var viewModel = CreateViewModel(
            new RecordingUserSettingsService(new UserSettings()),
            new RecordingThemeService(),
            dnsService,
            authenticationService);
        await viewModel.RefreshUserDataAsync();

        authenticationService.SetAuthenticated(false);

        Assert.False(viewModel.IsLoggedIn);
        Assert.False(viewModel.HasAdBlockingPreference);
        Assert.False(viewModel.AdBlockingRequestedEnabled);
        Assert.False(viewModel.CanUseAdBlocking);
        Assert.False(viewModel.AdBlockingEffectiveEnabled);
        Assert.Equal(string.Empty, viewModel.AdBlockingEffectiveMode);
        Assert.Equal(0, viewModel.AdBlockingPropagationSeconds);
        Assert.Equal(string.Empty, viewModel.AdBlockingStatus);
    }

    private static DnsPreferenceResponse Preference(
        bool requested,
        bool canUse,
        bool effective,
        string effectiveMode = "regular",
        int propagationSeconds = 10) =>
        new()
        {
            RequestedEnabled = requested,
            CanUseAdBlocking = canUse,
            EffectiveEnabled = effective,
            EffectiveMode = effectiveMode,
            PropagationSeconds = propagationSeconds
        };

    private static SettingsViewModel CreateViewModel(UserSettings settings)
    {
        return CreateViewModel(new RecordingUserSettingsService(settings), new RecordingThemeService());
    }

    private static SettingsViewModel CreateViewModel(
        RecordingUserSettingsService settingsService,
        RecordingThemeService themeService,
        RecordingDnsSettingsService? dnsSettingsService = null,
        StaticAuthenticationService? authenticationService = null,
        StaticAccountPlanService? accountPlanService = null)
    {
        return new SettingsViewModel(
            authenticationService ?? new StaticAuthenticationService(),
            accountPlanService ?? new StaticAccountPlanService(UserPlan.Pro),
            dnsSettingsService ?? new RecordingDnsSettingsService(),
            new RecordingNavigationService(),
            settingsService,
            themeService);
    }

    private sealed class RecordingThemeService : IThemeService
    {
        public AppThemePreference CurrentPreference { get; private set; } = AppThemePreference.System;
        public AppThemePreference? LastPreference { get; private set; }
        public int ApplyCount { get; private set; }

        public Task ApplyPreferenceAsync(AppThemePreference preference)
        {
            CurrentPreference = preference;
            LastPreference = preference;
            ApplyCount++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingUserSettingsService : IUserSettingsService
    {
        public RecordingUserSettingsService(UserSettings settings)
        {
            Settings = settings;
        }

        public UserSettings Settings { get; }
        public int SaveCount { get; private set; }
        public event EventHandler? SettingsChanged;

        public Task SaveSettingsAsync()
        {
            SaveCount++;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task LoadSettingsAsync() => Task.CompletedTask;
    }

    private sealed class RecordingNavigationService : INavigationService
    {
        public string CurrentView { get; private set; } = "settings";
        public event EventHandler<string>? ViewChanged;

        public void NavigateTo(string viewKey)
        {
            CurrentView = viewKey;
            ViewChanged?.Invoke(this, viewKey);
        }
    }

    private sealed class RecordingDnsSettingsService : IDnsSettingsService
    {
        public DnsPreferenceResponse? Preference { get; set; } =
            SettingsViewModelTests.Preference(false, true, false);

        public Func<CancellationToken, Task<DnsPreferenceResponse?>>? GetHandler { get; set; }
        public Func<bool, CancellationToken, Task<DnsPreferenceUpdateResult>>? SetHandler { get; set; }
        public TaskCompletionSource<bool> UpdateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool LastRequestedValue { get; private set; }
        public int GetCount { get; private set; }
        public int SetCount { get; private set; }

        public Task<DnsPreferenceResponse?> GetPreferenceAsync(CancellationToken ct = default)
        {
            GetCount++;
            return GetHandler?.Invoke(ct) ?? Task.FromResult(Preference);
        }

        public Task<DnsPreferenceUpdateResult> SetAdBlockingAsync(
            bool enabled,
            CancellationToken ct = default)
        {
            SetCount++;
            LastRequestedValue = enabled;
            UpdateStarted.TrySetResult(true);

            return SetHandler?.Invoke(enabled, ct) ??
                Task.FromResult(new DnsPreferenceUpdateResult
                {
                    Success = true,
                    Preference = SettingsViewModelTests.Preference(enabled, true, enabled)
                });
        }
    }

    private sealed class StaticSubscriptionService : ISubscriptionService
    {
        public Task<SubscriptionStatusResponse?> GetStatusAsync(CancellationToken ct = default) => Task.FromResult<SubscriptionStatusResponse?>(null);
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

    private sealed class StaticAccountPlanService : IAccountPlanService
    {
        public StaticAccountPlanService(UserPlan plan)
        {
            CurrentPlan = plan;
        }

        public UserPlan CurrentPlan { get; private set; }
        public bool IsPro => CurrentPlan == UserPlan.Pro;
        public bool IsOpenVpnAvailable => IsPro;
        public string CurrentPlanLabel => IsPro ? "Pro" : "Free";
        public bool IsRefreshing => false;
        public event Action? PlanChanged;

        public Task RefreshAsync(bool force = false, CancellationToken ct = default)
        {
            PlanChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void SetPlan(UserPlan plan)
        {
            CurrentPlan = plan;
            PlanChanged?.Invoke();
        }
    }

    private sealed class StaticAuthenticationService : IAuthenticationService
    {
        public event Action? SessionChanged;

        public bool IsAuthenticated { get; private set; } = true;
        public string? UserEmail => "theme@example.test";
        public string? UserId => "user-1";
        public UserPlan Plan => IsAuthenticated ? UserPlan.Pro : UserPlan.Free;

        public void SetAuthenticated(bool isAuthenticated)
        {
            IsAuthenticated = isAuthenticated;
            SessionChanged?.Invoke();
        }

        public void RaiseSessionChanged() => SessionChanged?.Invoke();

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
            SetAuthenticated(false);
            return Task.CompletedTask;
        }
    }
}
