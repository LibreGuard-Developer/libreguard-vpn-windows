using System.Windows;
using LibreGuard_VPN_Desktop.Views;

namespace LibreGuard_VPN_Desktop.Services;

public enum CardCheckoutPresentationResult
{
    Closed,
    ReturnDetected,
    Unavailable,
    OpenBrowserRequested
}

public interface ICardCheckoutPresenter
{
    Task<CardCheckoutPresentationResult> ShowAsync(Uri checkoutUri, CancellationToken cancellationToken = default);
}

internal sealed class CardCheckoutPresenter : ICardCheckoutPresenter
{
    private readonly ILoggerService _logger;

    public CardCheckoutPresenter(ILoggerService logger)
    {
        _logger = logger;
    }

    public Task<CardCheckoutPresentationResult> ShowAsync(
        Uri checkoutUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkoutUri);

        if (!CardCheckoutNavigationPolicy.IsAllowedWebUri(checkoutUri))
        {
            return Task.FromResult(ShowBrowserFallbackPrompt(
                "LibreGuard received an invalid checkout address."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(CardCheckoutPresentationResult.Closed);
        }

        try
        {
            var owner = Application.Current?.MainWindow;
            var window = new CardCheckoutWindow(checkoutUri, cancellationToken, _logger)
            {
                Owner = owner
            };

            window.ShowDialog();
            if (window.Result != CardCheckoutPresentationResult.Unavailable)
            {
                return Task.FromResult(window.Result);
            }

            return Task.FromResult(ShowBrowserFallbackPrompt(
                "Checkout could not open inside LibreGuard."));
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to create or show the embedded card checkout window.", ex);
            return Task.FromResult(ShowBrowserFallbackPrompt(
                "Checkout could not open inside LibreGuard."));
        }
    }

    private static CardCheckoutPresentationResult ShowBrowserFallbackPrompt(string message)
    {
        var choice = MessageBox.Show(
            Application.Current?.MainWindow,
            $"{message}\n\nWould you like to open it in your default browser instead?",
            "Card Checkout",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return choice == MessageBoxResult.Yes
            ? CardCheckoutPresentationResult.OpenBrowserRequested
            : CardCheckoutPresentationResult.Unavailable;
    }
}

internal static class CardCheckoutNavigationPolicy
{
    public static bool IsAllowedWebUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    public static bool IsCheckoutReturn(Uri? uri)
    {
        if (!IsAllowedWebUri(uri) || uri is null ||
            !uri.AbsolutePath.Equals("/Billing/Card", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        return query.TryGetValue("success", out var success) && success == "1" ||
               query.ContainsKey("checkout_id");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2
                ? Uri.UnescapeDataString(pair[1].Replace('+', ' '))
                : string.Empty;
            values[key] = value;
        }

        return values;
    }
}
