using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

internal sealed record TrayTooltipState(
    ConnectionStatus Status,
    string? Country,
    string? City,
    string? IpAddress,
    double SessionDataMb,
    UserPlan Plan,
    double MonthlyDataUsedMb,
    double MonthlyDataLimitMb);

internal static class TrayTooltipBuilder
{
    internal const int MaxTextLength = 63;
    private const double DefaultFreePlanLimitMb = 5 * 1024;

    public static string Build(TrayTooltipState state)
    {
        if (state.Status != ConnectionStatus.Connected)
            return state.Status switch
            {
                ConnectionStatus.Connecting => "LibreGuard VPN - Connecting",
                ConnectionStatus.Reconnecting => "LibreGuard VPN - Reconnecting",
                ConnectionStatus.Disconnecting => "LibreGuard VPN - Disconnecting",
                ConnectionStatus.Error => "LibreGuard VPN - Connection Error",
                _ => "LibreGuard VPN - Not Connected"
            };

        var location = BuildLocation(state.City, state.Country);
        var compactLocation = BuildCompactLocation(state.City, state.Country);
        var ip = string.IsNullOrWhiteSpace(state.IpAddress) ? null : state.IpAddress.Trim();
        var session = $"S {FormatAmount(state.SessionDataMb)}";
        var compactSession = FormatAmount(state.SessionDataMb);
        var monthly = state.Plan == UserPlan.Free
            ? $"M {FormatAmount(state.MonthlyDataUsedMb)}/{FormatAmount(GetEffectiveMonthlyLimit(state))}"
            : null;
        var compactMonthly = state.Plan == UserPlan.Free
            ? FormatUsageRatio(state.MonthlyDataUsedMb, GetEffectiveMonthlyLimit(state))
            : null;

        foreach (var candidate in BuildCandidates(location, compactLocation, ip, session, compactSession, monthly, compactMonthly))
        {
            if (candidate.Length <= MaxTextLength)
                return candidate;
        }

        return Truncate(BuildCandidates(location, compactLocation, ip, session, compactSession, monthly, compactMonthly).Last(), MaxTextLength);
    }

    private static IEnumerable<string> BuildCandidates(
        string? location,
        string? compactLocation,
        string? ip,
        string session,
        string compactSession,
        string? monthly,
        string? compactMonthly)
    {
        var fullSegments = new List<string>();
        if (!string.IsNullOrWhiteSpace(location))
            fullSegments.Add(location);
        if (!string.IsNullOrWhiteSpace(ip))
            fullSegments.Add(ip!);
        fullSegments.Add(session);
        if (!string.IsNullOrWhiteSpace(monthly))
            fullSegments.Add(monthly!);

        yield return Join("LibreGuard VPN - ", fullSegments);

        var compactSegments = new List<string>();
        if (!string.IsNullOrWhiteSpace(compactLocation))
            compactSegments.Add(compactLocation!);
        if (!string.IsNullOrWhiteSpace(ip))
            compactSegments.Add(ip!);
        compactSegments.Add(compactSession);
        if (!string.IsNullOrWhiteSpace(compactMonthly))
            compactSegments.Add(compactMonthly!);

        yield return Join("LibreGuard VPN - ", compactSegments);

        yield return Join("LibreGuard VPN - ", compactSegments.Take(3));

        if (!string.IsNullOrWhiteSpace(location))
            yield return $"LibreGuard VPN - {location}";

        yield return "LibreGuard VPN - Connected";
    }

    private static string Join(string prefix, IEnumerable<string> segments)
    {
        return prefix + string.Join(" | ", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
    }

    private static string BuildLocation(string? city, string? country)
    {
        if (string.IsNullOrWhiteSpace(city))
            return string.IsNullOrWhiteSpace(country) ? "Connected" : country.Trim();

        if (string.IsNullOrWhiteSpace(country))
            return city.Trim();

        return $"{city.Trim()}, {country.Trim()}";
    }

    private static string BuildCompactLocation(string? city, string? country)
    {
        if (string.IsNullOrWhiteSpace(city))
            return string.IsNullOrWhiteSpace(country) ? "Connected" : country.Trim();

        if (string.IsNullOrWhiteSpace(country))
            return city.Trim();

        return $"{city.Trim()},{country.Trim()}";
    }

    private static double GetEffectiveMonthlyLimit(TrayTooltipState state)
    {
        if (state.Plan != UserPlan.Free)
            return state.MonthlyDataLimitMb;

        return state.MonthlyDataLimitMb > 0 && state.MonthlyDataLimitMb < 1_000_000
            ? state.MonthlyDataLimitMb
            : DefaultFreePlanLimitMb;
    }

    private static string FormatAmount(double megabytes)
    {
        if (megabytes >= 1024)
            return $"{megabytes / 1024.0:F1}GB";

        if (megabytes >= 100)
            return $"{megabytes:F0}MB";

        return $"{megabytes:F1}MB";
    }

    private static string FormatUsageRatio(double usedMegabytes, double limitMegabytes)
    {
        if (usedMegabytes >= 1024 && limitMegabytes >= 1024)
            return $"{usedMegabytes / 1024.0:F1}/{limitMegabytes / 1024.0:F1}GB";

        return $"{FormatAmount(usedMegabytes)}/{FormatAmount(limitMegabytes)}";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        return value[..maxLength];
    }
}
