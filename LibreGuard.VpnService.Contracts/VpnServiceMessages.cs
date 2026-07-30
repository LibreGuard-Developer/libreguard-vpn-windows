using System.Text.Json.Serialization;

namespace LibreGuard.VpnService.Contracts;

/// <summary>
/// Well-known pipe name used by both service and client.
/// </summary>
public static class VpnServiceConstants
{
    public const string PipeName = "LibreGuardVpnService";
}

/// <summary>
/// Discriminated command types sent from the WPF app to the service.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VpnCommandType
{
    ImportCertificates,
    ImportTrustedRootCertificate,
    CreateConnection,
    SetIpsecPolicy,
    SetDnsServers,
    Dial,
    Disconnect,
    RemoveConnection,
    CleanupCertificates,
    Ping,
    StartOpenVpn,
    StopOpenVpn,
    GetOpenVpnStatus,
    GetOpenVpnHealth,
    ForceDisconnectAll,
    GetTunnelStatus,
    ShutdownService,
    EnableKillSwitch,
    DisableKillSwitch
}

/// <summary>
/// Request envelope sent over the named pipe.
/// </summary>
public sealed record VpnServiceRequest
{
    public required VpnCommandType Command { get; init; }

    /// <summary>
    /// Base64-encoded PFX bytes for <see cref="VpnCommandType.ImportCertificates"/>.
    /// </summary>
    public string? PfxBase64 { get; init; }

    /// <summary>
    /// PFX password for <see cref="VpnCommandType.ImportCertificates"/>.
    /// </summary>
    public string? PfxPassword { get; init; }

    /// <summary>
    /// Base64-encoded DER certificate bytes for <see cref="VpnCommandType.ImportTrustedRootCertificate"/>.
    /// </summary>
    public string? TrustedRootCertificateBase64 { get; init; }

    /// <summary>
    /// VPN connection name (e.g. "LibreGuard VPN").
    /// </summary>
    public string? ConnectionName { get; init; }

    /// <summary>
    /// Server address/hostname for <see cref="VpnCommandType.CreateConnection"/>.
    /// </summary>
    public string? ServerAddress { get; init; }

    /// <summary>
    /// Certificate thumbprints to clean up for <see cref="VpnCommandType.CleanupCertificates"/>.
    /// </summary>
    public string? ClientThumbprint { get; init; }

    /// <summary>
    /// CA cert thumbprint for <see cref="VpnCommandType.CleanupCertificates"/>.
    /// </summary>
    public string? CaThumbprint { get; init; }

    /// <summary>
    /// .ovpn config file content for <see cref="VpnCommandType.StartOpenVpn"/>.
    /// </summary>
    public string? OpenVpnConfigContent { get; init; }

    /// <summary>
    /// Passphrase for OpenVPN auth-user-pass for <see cref="VpnCommandType.StartOpenVpn"/>.
    /// </summary>
    public string? OpenVpnPassphrase { get; init; }

    /// <summary>
    /// The IP address of the VPN server to allow through the Kill Switch.
    /// </summary>
    public string? VpnServerIp { get; init; }

    /// <summary>
    /// The local IP address of the VPN interface to allow through the Kill Switch.
    /// </summary>
    public string? VpnLocalIp { get; init; }

    /// <summary>
    /// DNS server IP addresses to apply to the VPN interface.
    /// </summary>
    public string[]? DnsServers { get; init; }

    /// <summary>
    /// Windows interface index for DNS configuration when the client has already verified the VPN interface.
    /// </summary>
    public int? VpnInterfaceIndex { get; init; }

    /// <summary>
    /// Desktop client process ID that owns a started tunnel.
    /// </summary>
    public int? ClientProcessId { get; init; }

    /// <summary>
    /// UTC start time for <see cref="ClientProcessId"/> to avoid PID reuse.
    /// </summary>
    public DateTime? ClientProcessStartTimeUtc { get; init; }
}

/// <summary>
/// Response envelope sent back from the service.
/// </summary>
public sealed record VpnServiceResponse
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Client cert thumbprint returned by <see cref="VpnCommandType.ImportCertificates"/>.
    /// </summary>
    public string? ClientThumbprint { get; init; }

    /// <summary>
    /// CA cert thumbprint returned by <see cref="VpnCommandType.ImportCertificates"/>.
    /// </summary>
    public string? CaThumbprint { get; init; }

    /// <summary>
    /// Trusted root cert thumbprint returned by <see cref="VpnCommandType.ImportTrustedRootCertificate"/>.
    /// </summary>
    public string? TrustedRootThumbprint { get; init; }

    /// <summary>
    /// Exit code from rasdial or PowerShell for diagnostic purposes.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Stdout/stderr output for diagnostic purposes.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Current OpenVPN connection state for <see cref="VpnCommandType.GetOpenVpnStatus"/>.
    /// One of: Disconnected, Connecting, Authenticating, Connected, Reconnecting, Disconnecting, Error.
    /// </summary>
    public string? OpenVpnState { get; init; }

    /// <summary>
    /// Bytes received (downloaded) during the current OpenVPN session.
    /// </summary>
    public long BytesIn { get; init; }

    /// <summary>
    /// Bytes sent (uploaded) during the current OpenVPN session.
    /// </summary>
    public long BytesOut { get; init; }

    /// <summary>
    /// Local IP address assigned to the VPN tunnel interface.
    /// </summary>
    public string? VpnLocalIp { get; init; }

    /// <summary>
    /// Whether OpenVPN executable resolution found an executable.
    /// </summary>
    public bool OpenVpnInstalled { get; init; }

    /// <summary>
    /// Path to the OpenVPN executable selected by the service.
    /// </summary>
    public string? OpenVpnExePath { get; init; }

    /// <summary>
    /// Whether a supported OpenVPN network driver is installed.
    /// </summary>
    public bool OpenVpnDriverInstalled { get; init; }

    /// <summary>
    /// User-facing reason why OpenVPN setup or repair is required.
    /// </summary>
    public string? SetupRequiredReason { get; init; }

    /// <summary>
    /// True when the service detects any active LibreGuard tunnel.
    /// </summary>
    public bool TunnelActive { get; init; }

    /// <summary>
    /// True when OpenVPN is active or not fully disconnected.
    /// </summary>
    public bool OpenVpnActive { get; init; }

    /// <summary>
    /// True when the LibreGuard IKEv2/RAS connection is active.
    /// </summary>
    public bool IkeV2Active { get; init; }

    /// <summary>
    /// Human-readable tunnel status details for diagnostics.
    /// </summary>
    public string? TunnelStatus { get; init; }
}
