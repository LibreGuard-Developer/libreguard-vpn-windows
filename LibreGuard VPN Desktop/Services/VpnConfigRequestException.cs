using System.Net;

namespace LibreGuard_VPN_Desktop.Services;

internal sealed class VpnConfigRequestException : InvalidOperationException
{
    public VpnConfigRequestException(
        HttpStatusCode statusCode,
        string? backendMessage,
        string? errorCode,
        TimeSpan? retryAfter,
        string? rawBody)
        : base(BuildMessage(statusCode, backendMessage, errorCode))
    {
        StatusCode = statusCode;
        BackendMessage = backendMessage;
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
        RawBody = rawBody;
    }

    public HttpStatusCode StatusCode { get; }
    public string? BackendMessage { get; }
    public string? ErrorCode { get; }
    public TimeSpan? RetryAfter { get; }
    public string? RawBody { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string? backendMessage, string? errorCode)
    {
        var message = string.IsNullOrWhiteSpace(backendMessage)
            ? $"VPN configuration request failed with status {(int)statusCode} ({statusCode})."
            : backendMessage;

        return string.IsNullOrWhiteSpace(errorCode)
            ? message
            : $"{message} ({errorCode})";
    }
}
