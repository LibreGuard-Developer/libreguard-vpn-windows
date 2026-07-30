using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

internal enum TrayMenuActionKind
{
    None,
    QuickConnect,
    Disconnect,
    Exit,
    ConnectServer
}

internal sealed record TrayMenuAction(TrayMenuActionKind Kind, string? ServerId = null);

internal sealed record TrayMenuEntry(
    string Text,
    bool Enabled = true,
    TrayMenuAction? Action = null,
    IReadOnlyList<TrayMenuEntry>? Children = null,
    bool IsSeparator = false);

internal sealed record TrayMenuBuildState(
    bool IsAuthenticated,
    bool CanQuickConnect,
    bool IsConnected,
    bool IsPro,
    IReadOnlyList<ServerCountryGroup> Countries);

internal static class TrayMenuBuilder
{
    public static IReadOnlyList<TrayMenuEntry> Build(TrayMenuBuildState state)
    {
        var entries = new List<TrayMenuEntry>
        {
            BuildPrimaryConnectionEntry(state),
            BuildServersEntry(state),
            new(string.Empty, IsSeparator: true),
            new("Exit", true, new TrayMenuAction(TrayMenuActionKind.Exit))
        };

        return entries;
    }

    private static TrayMenuEntry BuildPrimaryConnectionEntry(TrayMenuBuildState state)
    {
        if (state.IsConnected)
            return new("Disconnect", state.IsAuthenticated, new TrayMenuAction(TrayMenuActionKind.Disconnect));

        return new("Quick Connect", state.IsAuthenticated && state.CanQuickConnect, new TrayMenuAction(TrayMenuActionKind.QuickConnect));
    }

    private static TrayMenuEntry BuildServersEntry(TrayMenuBuildState state)
    {
        if (state.IsConnected)
            return new("Servers", false);

        if (!state.IsAuthenticated)
            return new("Servers", false, Children: [new TrayMenuEntry("Sign in to view servers", false)]);

        if (state.Countries.Count == 0)
            return new("Servers", false, Children: [new TrayMenuEntry("No servers available", false)]);

        var countries = state.Countries
            .OrderBy(group => group.Country, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TrayMenuEntry(
                $"{group.Flag} {group.Country}".Trim(),
                true,
                Children: group.Servers
                    .OrderBy(server => server.City, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(server => server.ServerName, StringComparer.OrdinalIgnoreCase)
                    .Select(server => new TrayMenuEntry(
                        BuildServerLabel(server, state.IsPro),
                        state.IsPro || !server.IsPremium,
                        new TrayMenuAction(TrayMenuActionKind.ConnectServer, server.Id)))
                    .ToArray()))
            .ToArray();

        return new TrayMenuEntry("Servers", true, Children: countries);
    }

    private static string BuildServerLabel(ServerLocation server, bool isPro)
    {
        var location = string.IsNullOrWhiteSpace(server.City)
            ? server.ServerName
            : $"{server.City} - {server.ServerName}";

        if (!isPro && server.IsPremium)
            return $"{location} (Pro)";

        return location;
    }
}
