using System.Text.RegularExpressions;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Static utility for parsing and validating OpenVPN .ovpn configuration file content.
/// Detects embedded certificates, TLS auth, auth directives, and extracts connection parameters.
/// </summary>
internal static partial class OpenVpnConfigParser
{
    /// <summary>
    /// Extracts the remote host (IP or hostname) from the config.
    /// Returns null if no "remote" directive is found.
    /// </summary>
    public static string? ExtractRemoteHost(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        var match = RemoteDirectiveRegex().Match(configContent);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Extracts the remote port from the config.
    /// Returns null if no port is specified in the "remote" directive.
    /// </summary>
    public static int? ExtractRemotePort(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        var match = RemoteDirectiveRegex().Match(configContent);
        if (match.Success && match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var port))
            return port;

        return null;
    }

    /// <summary>
    /// Returns true if the config contains inline (embedded) certificate blocks: &lt;ca&gt;, &lt;cert&gt;, and &lt;key&gt;.
    /// </summary>
    public static bool HasEmbeddedCerts(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        return configContent.Contains("<ca>", StringComparison.Ordinal)
            && configContent.Contains("<cert>", StringComparison.Ordinal)
            && configContent.Contains("<key>", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true if the config has a &lt;tls-auth&gt; inline block or a tls-crypt directive.
    /// </summary>
    public static bool HasTlsAuth(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        return configContent.Contains("<tls-auth>", StringComparison.Ordinal)
            || configContent.Contains("<tls-crypt>", StringComparison.Ordinal)
            || TlsAuthDirectiveRegex().IsMatch(configContent)
            || TlsCryptDirectiveRegex().IsMatch(configContent);
    }

    /// <summary>
    /// Returns true if the config contains an auth-user-pass directive (with or without a file reference).
    /// </summary>
    public static bool HasAuthUserPass(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        return AuthUserPassRegex().IsMatch(configContent);
    }

    /// <summary>
    /// Validates that the config contains the minimum required directives for a client connection:
    /// "client" (or "tls-client"), "dev", and "remote".
    /// </summary>
    public static bool ValidateMinimalStructure(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        var hasClient = ClientDirectiveRegex().IsMatch(configContent);
        var hasDev = DevDirectiveRegex().IsMatch(configContent);
        var hasRemote = RemoteDirectiveRegex().IsMatch(configContent);

        return hasClient && hasDev && hasRemote;
    }

    /// <summary>
    /// Extracts the protocol from the "proto" directive (e.g., "udp", "tcp", "tcp-client").
    /// Returns null if no proto directive is found.
    /// </summary>
    public static string? ExtractProtocol(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        var match = ProtoDirectiveRegex().Match(configContent);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Extracts the device type from the "dev" directive (e.g., "tun", "tap").
    /// </summary>
    public static string? ExtractDeviceType(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        var match = DevDirectiveRegex().Match(configContent);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Returns true if the config references external certificate files (ca, cert, key directives
    /// with file paths) rather than embedded inline blocks.
    /// </summary>
    public static bool HasExternalCertReferences(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        return ExternalCaRegex().IsMatch(configContent)
            || ExternalCertRegex().IsMatch(configContent)
            || ExternalKeyRegex().IsMatch(configContent);
    }

    /// <summary>
    /// Removes backend-redacted passphrase directives that OpenVPN cannot resolve locally.
    /// The real passphrase is supplied separately through the management interface.
    /// </summary>
    public static string NormalizeForLaunch(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        var normalized = PlaceholderAskpassRegex().Replace(configContent, string.Empty);
        normalized = PlaceholderSetenvPassRegex().Replace(normalized, string.Empty);
        return ApplyPrivateDnsPolicy(normalized);
    }

    /// <summary>
    /// Replaces profile-provided DNS behavior with LibreGuard's mandatory private resolver.
    /// Server-pushed DNS values are ignored so public resolvers cannot become a fallback.
    /// </summary>
    internal static string ApplyPrivateDnsPolicy(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        var lineEnding = configContent.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : configContent.Contains('\r') && !configContent.Contains('\n')
                ? "\r"
                : "\n";
        var sourceLines = LineSeparatorRegex().Split(configContent);
        var normalizedLines = new List<string>(sourceLines.Length + 4);
        string? inlineBlockName = null;
        int? insertionIndex = null;

        foreach (var line in sourceLines)
        {
            if (inlineBlockName is not null)
            {
                normalizedLines.Add(line);
                if (IsInlineBlockEnd(line, inlineBlockName))
                    inlineBlockName = null;
                continue;
            }

            if (TryGetInlineBlockStart(line, out var blockName))
            {
                insertionIndex ??= normalizedLines.Count;
                inlineBlockName = blockName;
                normalizedLines.Add(line);
                continue;
            }

            if (DnsOptionDirectiveRegex().IsMatch(line) ||
                DnsServerDirectiveRegex().IsMatch(line) ||
                IsDnsPullFilterDirective(line) ||
                BlockOutsideDnsDirectiveRegex().IsMatch(line))
            {
                continue;
            }

            normalizedLines.Add(line);
        }

        var insertAt = insertionIndex ?? normalizedLines.Count;
        if (insertionIndex is null)
        {
            while (insertAt > 0 && string.IsNullOrWhiteSpace(normalizedLines[insertAt - 1]))
                insertAt--;
        }

        normalizedLines.InsertRange(insertAt,
        [
            $"dhcp-option DNS {VpnDnsPolicy.ResolverAddress}",
            "pull-filter ignore \"dhcp-option DNS\"",
            "pull-filter ignore \"dhcp-option DNS6\"",
            "pull-filter ignore \"dns \"",
            "block-outside-dns"
        ]);

        return string.Join(lineEnding, normalizedLines);
    }

    private static bool TryGetInlineBlockStart(string line, out string? blockName)
    {
        var trimmed = line.Trim();
        var candidateName = trimmed.Length > 2 ? trimmed[1..^1] : string.Empty;
        if (trimmed.Length > 2 &&
            trimmed[0] == '<' &&
            trimmed[1] != '/' &&
            trimmed[^1] == '>' &&
            candidateName.IndexOf(' ') < 0 &&
            candidateName.IndexOf('\t') < 0 &&
            candidateName.IndexOf('<') < 0 &&
            candidateName.IndexOf('>') < 0)
        {
            blockName = candidateName;
            return true;
        }

        blockName = null;
        return false;
    }

    private static bool IsInlineBlockEnd(string line, string blockName) =>
        string.Equals(line.Trim(), $"</{blockName}>", StringComparison.OrdinalIgnoreCase);

    private static bool IsDnsPullFilterDirective(string line)
    {
        var match = PullFilterDirectiveRegex().Match(line);
        if (!match.Success)
            return false;

        var pattern = match.Groups["doubleQuoted"].Success
            ? match.Groups["doubleQuoted"].Value
            : match.Groups["singleQuoted"].Success
                ? match.Groups["singleQuoted"].Value
                : match.Groups["unquoted"].Value;
        pattern = pattern.Trim();

        // OpenVPN uses prefix matching and stops at the first matching pull filter.
        // Remove both specific DNS filters and broader prefixes (including an empty
        // accept filter) that would otherwise take precedence over the filters below.
        return PullFilterCanMatchOption(pattern, "dhcp-option DNS") ||
               PullFilterCanMatchOption(pattern, "dhcp-option DNS6") ||
               PullFilterCanMatchOption(pattern, "dns");
    }

    private static bool PullFilterCanMatchOption(string pattern, string optionName) =>
        optionName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
        pattern.StartsWith($"{optionName} ", StringComparison.OrdinalIgnoreCase);

    // Regex patterns for parsing OpenVPN config directives.
    // Each regex matches a directive at the start of a line, ignoring comment lines (starting with # or ;).

    [GeneratedRegex(@"^remote\s+(\S+)(?:\s+(\d+))?", RegexOptions.Multiline)]
    private static partial Regex RemoteDirectiveRegex();

    [GeneratedRegex(@"^(?:client|tls-client)\s*$", RegexOptions.Multiline)]
    private static partial Regex ClientDirectiveRegex();

    [GeneratedRegex(@"^dev\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex DevDirectiveRegex();

    [GeneratedRegex(@"^proto\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex ProtoDirectiveRegex();

    [GeneratedRegex(@"^tls-auth\s+\S+", RegexOptions.Multiline)]
    private static partial Regex TlsAuthDirectiveRegex();

    [GeneratedRegex(@"^tls-crypt\s+\S+", RegexOptions.Multiline)]
    private static partial Regex TlsCryptDirectiveRegex();

    [GeneratedRegex(@"^auth-user-pass", RegexOptions.Multiline)]
    private static partial Regex AuthUserPassRegex();

    [GeneratedRegex(@"^ca\s+\S+", RegexOptions.Multiline)]
    private static partial Regex ExternalCaRegex();

    [GeneratedRegex(@"^cert\s+\S+", RegexOptions.Multiline)]
    private static partial Regex ExternalCertRegex();

    [GeneratedRegex(@"^key\s+\S+", RegexOptions.Multiline)]
    private static partial Regex ExternalKeyRegex();

    [GeneratedRegex(@"^\s*askpass\s+\[ENCRYPTED_PASSPHRASE\]\s*(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PlaceholderAskpassRegex();

    [GeneratedRegex(@"^\s*setenv\s+[^\r\n]*(?:PASS|PASSWORD)[^\r\n]*\s+\[ENCRYPTED_PASSPHRASE\]\s*(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PlaceholderSetenvPassRegex();

    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex LineSeparatorRegex();

    [GeneratedRegex(@"^\s*(?:--)?dhcp-option\s+(?:""DNS6?""|'DNS6?'|DNS6?)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DnsOptionDirectiveRegex();

    [GeneratedRegex(@"^\s*(?:--)?dns\s+server(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DnsServerDirectiveRegex();

    [GeneratedRegex(@"^\s*(?:--)?pull-filter\s+(?:accept|ignore|reject)\s+(?:""(?<doubleQuoted>[^""]*)""|'(?<singleQuoted>[^']*)'|(?<unquoted>[^#;]*?))(?:\s*[#;].*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PullFilterDirectiveRegex();

    [GeneratedRegex(@"^\s*(?:--)?block-outside-dns(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BlockOutsideDnsDirectiveRegex();
}
