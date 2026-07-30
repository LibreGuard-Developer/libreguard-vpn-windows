using System.Diagnostics;
using System.Windows;
using LibreGuard_VPN_Desktop.Services;
using Microsoft.Web.WebView2.Core;

namespace LibreGuard_VPN_Desktop.Views;

public partial class CardCheckoutWindow : Window
{
    private readonly Uri _checkoutUri;
    private readonly CancellationToken _cancellationToken;
    private readonly ILoggerService _logger;
    private CancellationTokenRegistration _cancellationRegistration;

    public CardCheckoutPresentationResult Result { get; private set; } = CardCheckoutPresentationResult.Closed;

    public CardCheckoutWindow(
        Uri checkoutUri,
        CancellationToken cancellationToken,
        ILoggerService logger)
    {
        _checkoutUri = checkoutUri;
        _cancellationToken = cancellationToken;
        _logger = logger;

        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            Close();
            return;
        }

        _cancellationRegistration = _cancellationToken.Register(() =>
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible)
                {
                    Close();
                }
            }));

        try
        {
            var environment = await CardCheckoutWebView2Environment.CreateAsync();
            await CheckoutBrowser.EnsureCoreWebView2Async(environment);
            ConfigureBrowser(CheckoutBrowser.CoreWebView2);
            CheckoutBrowser.Source = _checkoutUri;
        }
        catch (Exception ex)
        {
            _logger.LogError("Embedded card checkout WebView2 initialization failed.", ex);
            Result = CardCheckoutPresentationResult.Unavailable;
            Close();
        }
    }

    private void ConfigureBrowser(CoreWebView2 browser)
    {
        browser.Settings.AreDevToolsEnabled = false;
        browser.Settings.AreDefaultContextMenusEnabled = false;
        browser.Settings.IsStatusBarEnabled = false;
        browser.Settings.AreBrowserAcceleratorKeysEnabled = false;

        browser.NavigationStarting += OnNavigationStarting;
        browser.NavigationCompleted += OnNavigationCompleted;
        browser.NewWindowRequested += OnNewWindowRequested;
        browser.DownloadStarting += OnDownloadStarting;
        browser.ProcessFailed += OnProcessFailed;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        if (CardCheckoutNavigationPolicy.IsCheckoutReturn(uri))
        {
            e.Cancel = true;
            Result = CardCheckoutPresentationResult.ReturnDetected;
            Close();
            return;
        }

        if (!CardCheckoutNavigationPolicy.IsAllowedWebUri(uri))
        {
            e.Cancel = true;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (!e.IsSuccess)
        {
            _logger.LogWarning($"Embedded card checkout navigation failed: {e.WebErrorStatus}.");
            Result = CardCheckoutPresentationResult.Unavailable;
            Close();
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            CardCheckoutNavigationPolicy.IsAllowedWebUri(uri))
        {
            CheckoutBrowser.Source = uri;
        }
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.LogWarning($"Embedded card checkout WebView2 process failed: {e.ProcessFailedKind}.");
        Result = CardCheckoutPresentationResult.Unavailable;
        Close();
    }

    private void CloseAndOpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _checkoutUri.AbsoluteUri,
                UseShellExecute = true
            });
            Result = CardCheckoutPresentationResult.OpenBrowserRequested;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to open card checkout in the default browser.", ex);
        }

        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _cancellationRegistration.Dispose();
        Loaded -= OnLoaded;
        Closed -= OnClosed;

        if (CheckoutBrowser.CoreWebView2 is { } browser)
        {
            browser.NavigationStarting -= OnNavigationStarting;
            browser.NavigationCompleted -= OnNavigationCompleted;
            browser.NewWindowRequested -= OnNewWindowRequested;
            browser.DownloadStarting -= OnDownloadStarting;
            browser.ProcessFailed -= OnProcessFailed;
        }

        CheckoutBrowser.Dispose();
    }
}
