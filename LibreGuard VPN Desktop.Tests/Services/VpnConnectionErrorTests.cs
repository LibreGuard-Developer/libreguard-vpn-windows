using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;
using Xunit;

namespace LibreGuard_VPN_Desktop.Tests.Services;

/// <summary>
/// Tests for VPN config response deserialization and connection error classification.
/// </summary>
public class VpnConnectionErrorTests
{
    #region VpnConfigResponse deserialization

    [Fact]
    public void VpnConfigResponse_DeserializesSuccessPayload()
    {
        // Arrange
        var json = """
            {
                "success": true,
                "protocol": "openvpn",
                "serverName": "EU-Amsterdam-01",
                "serverIp": "185.199.108.42",
                "certificateName": "client-cert-abc",
                "configContent": "client\ndev tun\nproto udp\nremote 185.199.108.42 1194",
                "passphrase": "s3cret",
                "issueDate": "2025-01-01T00:00:00Z",
                "expirationDate": "2025-07-01T00:00:00Z",
                "clientIp": "10.8.0.5",
                "deviceId": "device-xyz-123"
            }
            """;

        // Act
        var response = JsonSerializer.Deserialize<VpnConfigResponse>(json);

        // Assert
        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Equal("openvpn", response.Protocol);
        Assert.Equal("EU-Amsterdam-01", response.ServerName);
        Assert.Equal("185.199.108.42", response.ServerIp);
        Assert.Equal("client-cert-abc", response.CertificateName);
        Assert.Contains("client", response.ConfigContent);
        Assert.Equal("s3cret", response.Passphrase);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), response.IssueDate);
        Assert.Equal(new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc), response.ExpirationDate);
        Assert.Equal("10.8.0.5", response.ClientIp);
        Assert.Equal("device-xyz-123", response.DeviceId);
    }

    [Fact]
    public void VpnConfigResponse_DeserializesWithNullPassphrase()
    {
        // Arrange
        var json = """
            {
                "success": true,
                "protocol": "ikev2",
                "serverName": "US-NewYork-02",
                "serverIp": "192.168.1.1",
                "certificateName": "cert-123",
                "configContent": "config-content-here",
                "passphrase": null,
                "issueDate": "2025-06-01T00:00:00Z",
                "expirationDate": "2025-12-01T00:00:00Z",
                "clientIp": "10.0.0.2",
                "deviceId": "dev-001"
            }
            """;

        // Act
        var response = JsonSerializer.Deserialize<VpnConfigResponse>(json);

        // Assert
        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Null(response.Passphrase);
    }

    [Fact]
    public void VpnConfigResponse_DeserializesEncryptedPassphrasePayload()
    {
        // Arrange
        var json = """
            {
                "success": true,
                "protocol": "IKEV2",
                "serverName": "US-NewYork-02",
                "serverIp": "192.168.1.1",
                "certificateName": "IKEV2_client42",
                "configContent": "{\"local\":{\"p12\":\"abc\"}}",
                "encryptedPassphrase": {
                    "algorithm": "RSA-OAEP-256",
                    "keyId": "abc123",
                    "ciphertext": "ZmFrZQ=="
                },
                "issueDate": "2025-06-01T00:00:00Z",
                "expirationDate": "2025-12-01T00:00:00Z",
                "clientIp": "10.0.0.2",
                "deviceId": "dev-001"
            }
            """;

        // Act
        var response = JsonSerializer.Deserialize<VpnConfigResponse>(json);

        // Assert
        Assert.NotNull(response);
        Assert.Null(response.Passphrase);
        Assert.NotNull(response.EncryptedPassphrase);
        Assert.Equal("RSA-OAEP-256", response.EncryptedPassphrase.Algorithm);
        Assert.Equal("abc123", response.EncryptedPassphrase.KeyId);
        Assert.Equal("ZmFrZQ==", response.EncryptedPassphrase.Ciphertext);
    }

    [Fact]
    public void VpnConfigResponse_DeserializesFailureResponse()
    {
        // Arrange
        var json = """
            {
                "success": false,
                "protocol": "",
                "serverName": "",
                "serverIp": "",
                "certificateName": "",
                "configContent": "",
                "passphrase": null,
                "issueDate": "0001-01-01T00:00:00",
                "expirationDate": "0001-01-01T00:00:00",
                "clientIp": "",
                "deviceId": ""
            }
            """;

        // Act
        var response = JsonSerializer.Deserialize<VpnConfigResponse>(json);

        // Assert
        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.Empty(response.ConfigContent);
    }

    [Fact]
    public void VpnConfigResponse_DeserializesMissingOptionalFields()
    {
        // Arrange - minimal payload with only required fields
        var json = """{ "success": true }""";

        // Act
        var response = JsonSerializer.Deserialize<VpnConfigResponse>(json);

        // Assert
        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Equal(string.Empty, response.Protocol);
        Assert.Equal(string.Empty, response.ServerName);
        Assert.Null(response.Passphrase);
    }

    [Fact]
    public void VpnConfigResponse_ThrowsOnMalformedJson()
    {
        // Arrange
        var json = "{ not valid json }}}";

        // Act & Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VpnConfigResponse>(json));
    }

    [Fact]
    public void DataQuotaResponse_DeserializesUnlimitedQuotaWithNullFields()
    {
        var json = """
            {
                "bytesUsed": 12345,
                "bytesLimit": null,
                "bytesRemaining": null,
                "usagePercentage": null,
                "isUnlimited": true,
                "isOverLimit": false,
                "formattedUsed": "12.06 KB",
                "formattedLimit": null,
                "formattedRemaining": null
            }
            """;

        var response = JsonSerializer.Deserialize<DataQuotaResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(12345, response.BytesUsed);
        Assert.Null(response.BytesLimit);
        Assert.Null(response.BytesRemaining);
        Assert.Null(response.UsagePercentage);
        Assert.True(response.IsUnlimited);
    }

    [Fact]
    public void CanConnectResponse_DeserializesDeniedPayload()
    {
        var json = """
            {
                "allowed": false,
                "reason": "Data limit exceeded for this billing period",
                "message": "You have used 5.03 GB of your 5.00 GB monthly limit.",
                "bytesUsed": 5400000000,
                "bytesLimit": 5368709120,
                "resetDate": "2024-02-01T00:00:00Z",
                "isUnlimited": false
            }
            """;

        var response = JsonSerializer.Deserialize<CanConnectResponse>(json);

        Assert.NotNull(response);
        Assert.False(response.Allowed);
        Assert.Equal("Data limit exceeded for this billing period", response.Reason);
        Assert.Contains("monthly limit", response.Message);
        Assert.Equal(5400000000, response.BytesUsed);
        Assert.Equal(5368709120, response.BytesLimit);
        Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), response.ResetDate);
        Assert.False(response.IsUnlimited);
    }

    #endregion

    #region ClassifyConnectionError

    [Fact]
    public void ClassifyConnectionError_FileNotFoundException_ReturnsOpenVpnNotInstalled()
    {
        // Arrange
        var ex = new FileNotFoundException("openvpn.exe not found");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("OpenVPN is not installed", result);
        Assert.Contains("bundled setup", result);
    }

    [Fact]
    public void ClassifyConnectionError_TapMessage_ReturnsTapAdapterMissing()
    {
        // Arrange
        var ex = new InvalidOperationException("TAP network adapter not found on this system");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("TAP network adapter", result);
    }

    [Fact]
    public void ClassifyConnectionError_AuthFailed_ReturnsAuthError()
    {
        // Arrange
        var ex = new InvalidOperationException("AUTH_FAILED: invalid credentials");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("authentication failed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyConnectionError_AuthenticationFailed_ReturnsAuthError()
    {
        // Arrange
        var ex = new InvalidOperationException("VPN authentication failed during handshake");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("authentication failed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyConnectionError_CertificateExpired_ReturnsCertError()
    {
        // Arrange
        var ex = new InvalidOperationException("certificate has expired, VERIFY ERROR depth 0");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("certificate", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expired", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyConnectionError_VerifyError_ReturnsCertError()
    {
        // Arrange
        var ex = new InvalidOperationException("VERIFY ERROR: certificate chain failed");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("certificate", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyConnectionError_403Forbidden_ReturnsSubscriptionRequired()
    {
        // Arrange
        var ex = new HttpRequestException("Server returned status code 403");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("subscription", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyConnectionError_DataLimitMessage_ReturnsQuotaMessage()
    {
        var ex = new InvalidOperationException("You have used 5.03 GB of your 5.00 GB monthly limit.");

        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        Assert.Contains("monthly limit", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subscription", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyConnectionError_IkeInterfaceVerificationMessage_ReturnsOriginalMessage()
    {
        var ex = new InvalidOperationException(
            "IKEv2 connected, but Windows did not expose a usable LibreGuard VPN interface.");

        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        Assert.Contains("usable LibreGuard VPN interface", result);
    }

    [Fact]
    public void ClassifyConnectionError_401Unauthorized_ReturnsSessionExpired()
    {
        // Arrange
        var ex = new HttpRequestException("Server returned 401 unauthorized");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("Session expired", result);
    }

    [Fact]
    public void ClassifyConnectionError_404NotFound_ReturnsConfigNotAvailable()
    {
        // Arrange
        var ex = new HttpRequestException("Response status code: 404");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("not available", result);
    }

    [Fact]
    public void ClassifyConnectionError_UnknownError_IncludesOriginalMessage()
    {
        // Arrange
        var ex = new InvalidOperationException("Something completely unexpected happened");

        // Act
        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        // Assert
        Assert.Contains("Connection failed", result);
        Assert.Contains("Something completely unexpected happened", result);
    }

    [Fact]
    public void ClassifyConnectionError_DeviceKeyRequired_ReturnsKeyRegistrationMessage()
    {
        var ex = new VpnConfigRequestException(
            HttpStatusCode.Conflict,
            "Device key registration is required before retrieving this VPN configuration.",
            "DEVICE_KEY_REQUIRED",
            retryAfter: null,
            rawBody: null);

        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        Assert.Contains("sign in again", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyConnectionError_PassphraseUnavailable_ReturnsCertificateRenewalMessage()
    {
        var ex = new VpnConfigRequestException(
            HttpStatusCode.Conflict,
            "Certificate passphrase is unavailable.",
            "PASSPHRASE_UNAVAILABLE",
            retryAfter: null,
            rawBody: null);

        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        Assert.Contains("passphrase is unavailable", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new certificate", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyConnectionError_ConfigBusy_IncludesRetryAfter()
    {
        var ex = new VpnConfigRequestException(
            HttpStatusCode.TooManyRequests,
            "VPN configuration retrieval is busy. Please retry shortly.",
            "VPN_CONFIG_BUSY",
            TimeSpan.FromSeconds(3),
            rawBody: null);

        var result = WinVpnConnectionService.ClassifyConnectionError(ex);

        Assert.Contains("retry in 3 seconds", result, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region VpnConfigRequestException parsing

    [Fact]
    public async Task CreateRequestExceptionAsync_ParsesBackendErrorBody()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""
                {
                    "message": "Device key registration is required before retrieving this VPN configuration.",
                    "errorCode": "DEVICE_KEY_REQUIRED"
                }
                """)
        };

        var ex = await ApiVpnConfigService.CreateRequestExceptionAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal("DEVICE_KEY_REQUIRED", ex.ErrorCode);
        Assert.Contains("Device key registration", ex.BackendMessage);
    }

    [Fact]
    public async Task CreateRequestExceptionAsync_UsesRetryAfterHeader()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{ "message": "busy", "errorCode": "VPN_CONFIG_BUSY" }""")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        var ex = await ApiVpnConfigService.CreateRequestExceptionAsync(response);

        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    #endregion

    #region VpnConfigRequest serialization

    [Fact]
    public void VpnConfigRequest_SerializesCorrectly()
    {
        // Arrange
        var request = new VpnConfigRequest { ServerId = 42, Protocol = "openvpn" };

        // Act
        var json = JsonSerializer.Serialize(request);

        // Assert
        Assert.Contains("\"serverId\":42", json);
        Assert.Contains("\"protocol\":\"openvpn\"", json);
    }

    #endregion
}
