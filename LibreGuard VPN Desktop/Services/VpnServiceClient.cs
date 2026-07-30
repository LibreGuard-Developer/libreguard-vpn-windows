using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Named pipe client that communicates with the LibreGuard VPN Service.
/// The service runs as LocalSystem and handles all privileged operations
/// (cert import, VPN entry creation, IPsec config, rasdial) — no UAC needed.
/// </summary>
public interface IVpnServiceClient
{
    Task<VpnServiceResponse> SendAsync(VpnServiceRequest request, CancellationToken ct = default);
    Task<bool> IsServiceAvailableAsync(CancellationToken ct = default);
}

internal sealed class VpnServiceClient : IVpnServiceClient
{
    /// <summary>
    /// Well-known pipe name shared between the WPF app and the Windows service.
    /// </summary>
    internal const string PipeName = "LibreGuardVpnService";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly Lazy<(int ProcessId, DateTime StartTimeUtc)> ClientProcessIdentity = new(GetClientProcessIdentity);

    private const int ConnectTimeoutMs = 5000;
    private const int AvailabilityTimeoutMs = 1500;
    private const int MaxRetries = 1;
    private const int RetryDelayMs = 200;

    /// <summary>
    /// Sends a request to the VPN service and returns the response.
    /// Opens a new pipe connection for each request (one-shot messaging).
    /// Retries once on transient pipe errors (e.g. service recycling its listener between requests).
    /// </summary>
    public async Task<VpnServiceResponse> SendAsync(VpnServiceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = AttachClientIdentity(request);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(request, ct);
            }
            catch (InvalidOperationException) when (attempt < MaxRetries)
            {
                Debug.WriteLine($"[VpnServiceClient] {request.Command} attempt {attempt + 1} failed, retrying in {RetryDelayMs}ms");
                await Task.Delay(RetryDelayMs, ct);
            }
        }
    }

    private static VpnServiceRequest AttachClientIdentity(VpnServiceRequest request)
    {
        var identity = ClientProcessIdentity.Value;
        return request with
        {
            ClientProcessId = request.ClientProcessId ?? identity.ProcessId,
            ClientProcessStartTimeUtc = request.ClientProcessStartTimeUtc ?? identity.StartTimeUtc
        };
    }

    private static (int ProcessId, DateTime StartTimeUtc) GetClientProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return (process.Id, process.StartTime.ToUniversalTime());
    }

    private async Task<VpnServiceResponse> SendOnceAsync(VpnServiceRequest request, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "Could not connect to the LibreGuard VPN Service. " +
                "Ensure the service is installed and running (sc query LibreGuardVpnService).");
        }

        // Write length-prefixed request
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var lengthBytes = BitConverter.GetBytes(requestBytes.Length);
        await pipe.WriteAsync(lengthBytes, ct);
        await pipe.WriteAsync(requestBytes, ct);
        await pipe.FlushAsync(ct);

        // Read length-prefixed response
        var responseLengthBuf = new byte[4];
        var bytesRead = await ReadExactAsync(pipe, responseLengthBuf, ct);
        if (bytesRead < 4)
            throw new InvalidOperationException("VPN Service returned an incomplete response.");

        var responseLength = BitConverter.ToInt32(responseLengthBuf, 0);
        if (responseLength is <= 0 or > 1_048_576)
            throw new InvalidOperationException($"VPN Service returned invalid response length: {responseLength}");

        var responseBuf = new byte[responseLength];
        bytesRead = await ReadExactAsync(pipe, responseBuf, ct);
        if (bytesRead < responseLength)
            throw new InvalidOperationException("VPN Service returned a truncated response.");

        var response = JsonSerializer.Deserialize<VpnServiceResponse>(responseBuf, JsonOptions)
            ?? throw new InvalidOperationException("VPN Service returned null response.");

        Debug.WriteLine($"[VpnServiceClient] {request.Command} ? Success={response.Success}, Output={response.Output}");
        return response;
    }

    /// <summary>
    /// Checks whether the VPN service is running and reachable.
    /// </summary>
    public async Task<bool> IsServiceAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(AvailabilityTimeoutMs);

            var response = await SendAsync(new VpnServiceRequest { Command = VpnCommandType.Ping }, timeoutCts.Token);
            return response.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (bytesRead == 0) break;
            totalRead += bytesRead;
        }

        return totalRead;
    }
}

/// <summary>
/// Command types sent from the WPF app to the VPN service.
/// Must stay in sync with the service's copy.
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
    public string? PfxBase64 { get; init; }
    public string? PfxPassword { get; init; }
    public string? TrustedRootCertificateBase64 { get; init; }
    public string? ConnectionName { get; init; }
    public string? ServerAddress { get; init; }
    public string? ClientThumbprint { get; init; }
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
    /// VPN server IP to allow through the Kill Switch firewall rules.
    /// </summary>
    public string? VpnServerIp { get; init; }

    /// <summary>
    /// Local VPN tunnel IP to allow through the Kill Switch firewall rules.
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
    public int? ClientProcessId { get; init; }
    public DateTime? ClientProcessStartTimeUtc { get; init; }
}

/// <summary>
/// Response envelope sent back from the service.
/// </summary>
public sealed record VpnServiceResponse
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ClientThumbprint { get; init; }
    public string? CaThumbprint { get; init; }
    public string? TrustedRootThumbprint { get; init; }
    public int ExitCode { get; init; }
    public string? Output { get; init; }

    /// <summary>
    /// Current OpenVPN connection state for <see cref="VpnCommandType.GetOpenVpnStatus"/>.
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

    public bool OpenVpnInstalled { get; init; }
    public string? OpenVpnExePath { get; init; }
    public bool OpenVpnDriverInstalled { get; init; }
    public string? SetupRequiredReason { get; init; }
    public bool TunnelActive { get; init; }
    public bool OpenVpnActive { get; init; }
    public bool IkeV2Active { get; init; }
    public string? TunnelStatus { get; init; }
}

