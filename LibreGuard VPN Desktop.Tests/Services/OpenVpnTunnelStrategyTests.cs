using LibreGuard_VPN_Desktop.Services;
using Xunit;

namespace LibreGuard_VPN_Desktop.Tests.Services;

/// <summary>
/// Tests for <see cref="OpenVpnTunnelStrategy"/> thin pipe client.
/// Connect/disconnect tests require the LibreGuard VPN Service running; skip in CI.
/// Run manually with: dotnet test --filter "Category=Integration"
/// </summary>
public class OpenVpnTunnelStrategyTests : IDisposable
{
    private readonly VpnServiceClient _serviceClient;
    private readonly OpenVpnTunnelStrategy _strategy;

    public OpenVpnTunnelStrategyTests()
    {
        _serviceClient = new VpnServiceClient();
        _strategy = new OpenVpnTunnelStrategy(_serviceClient);
    }

    public void Dispose()
    {
        _strategy.Dispose();
    }

    [Fact]
    public void Protocol_ReturnsOpenVpn()
    {
        // Act & Assert
        Assert.Equal(Models.VpnProtocol.OpenVPN, _strategy.Protocol);
    }

    [Fact]
    public void IsConnected_WhenNotStarted_ReturnsFalse()
    {
        // Act & Assert
        Assert.False(_strategy.IsConnected);
    }

    [Fact]
    public void ConnectionState_WhenNotStarted_IsDisconnected()
    {
        // Act & Assert
        Assert.Equal(OpenVpnConnectionState.Disconnected, _strategy.ConnectionState);
    }

    [Fact]
    public void Constructor_WithNullServiceClient_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new OpenVpnTunnelStrategy(null!));
    }

    [Fact]
    public void Dispose_WhenNotConnected_DoesNotThrow()
    {
        // Arrange
        var client = new VpnServiceClient();
        var strategy = new OpenVpnTunnelStrategy(client);

        // Act & Assert
        var exception = Record.Exception(() => strategy.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_SendsStopOpenVpn()
    {
        var client = new RecordingVpnServiceClient();
        var strategy = new OpenVpnTunnelStrategy(client);

        await strategy.DisconnectAsync();

        var command = Assert.Single(client.Requests);
        Assert.Equal(VpnCommandType.StopOpenVpn, command.Command);
        Assert.False(strategy.IsConnected);
        Assert.Equal(OpenVpnConnectionState.Disconnected, strategy.ConnectionState);
    }

    [Fact]
    public async Task DisconnectAsync_WhenServiceFails_Throws()
    {
        var client = new RecordingVpnServiceClient
        {
            Response = new VpnServiceResponse
            {
                Success = false,
                ErrorMessage = "stop failed"
            }
        };
        var strategy = new OpenVpnTunnelStrategy(client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.DisconnectAsync());

        Assert.Contains("stop failed", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_WithPlaceholderPassphraseDirectives_SendsCleanedConfigAndSeparatePassphrase()
    {
        var client = new RecordingVpnServiceClient();
        using var strategy = new OpenVpnTunnelStrategy(client);
        var tempConfig = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.ovpn");
        await File.WriteAllTextAsync(tempConfig, """
            client
            dev tun
            remote vpn.example.com 1194
            askpass [ENCRYPTED_PASSPHRASE]
            setenv CLIENT_PASSWORD [ENCRYPTED_PASSPHRASE]
            auth-user-pass
            dhcp-option DNS 1.1.1.1
            dhcp-option DNS 10.254.0.54
            dhcp-option DNS6 2606:4700:4700::1111
            --dns server -1 address 10.254.0.54
            """);

        try
        {
            await strategy.ConnectAsync(tempConfig, "secret-passphrase", "vpn.example.com");

            Assert.Equal(2, client.Requests.Count);
            Assert.Equal(VpnCommandType.StopOpenVpn, client.Requests[0].Command);
            var startRequest = client.Requests[1];
            Assert.Equal(VpnCommandType.StartOpenVpn, startRequest.Command);
            Assert.DoesNotContain("[ENCRYPTED_PASSPHRASE]", startRequest.OpenVpnConfigContent);
            Assert.Contains("auth-user-pass", startRequest.OpenVpnConfigContent);
            Assert.Contains("dhcp-option DNS 10.254.0.53", startRequest.OpenVpnConfigContent);
            Assert.DoesNotContain("dhcp-option DNS 1.1.1.1", startRequest.OpenVpnConfigContent);
            Assert.DoesNotContain("dhcp-option DNS 10.254.0.54", startRequest.OpenVpnConfigContent);
            Assert.DoesNotContain("2606:4700:4700::1111", startRequest.OpenVpnConfigContent);
            Assert.DoesNotContain("--dns server", startRequest.OpenVpnConfigContent);
            Assert.Contains("pull-filter ignore \"dhcp-option DNS\"", startRequest.OpenVpnConfigContent);
            Assert.Contains("pull-filter ignore \"dhcp-option DNS6\"", startRequest.OpenVpnConfigContent);
            Assert.Contains("pull-filter ignore \"dns \"", startRequest.OpenVpnConfigContent);
            Assert.Contains("block-outside-dns", startRequest.OpenVpnConfigContent);
            Assert.Equal("secret-passphrase", startRequest.OpenVpnPassphrase);
        }
        finally
        {
            try { File.Delete(tempConfig); } catch { /* best-effort cleanup */ }
        }
    }

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires LibreGuard VPN Service running")]
    public async Task ConnectAsync_WithInvalidConfig_ThrowsOrReportsError()
    {
        // Arrange
        var tempConfig = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.ovpn");
        await File.WriteAllTextAsync(tempConfig, "client\ndev tun\nremote 127.0.0.1 1194\nproto udp");

        try
        {
            // Act & Assert
            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => _strategy.ConnectAsync(tempConfig, null, "127.0.0.1"));

            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        finally
        {
            try { File.Delete(tempConfig); } catch { /* best-effort cleanup */ }
        }
    }

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires LibreGuard VPN Service running")]
    public async Task DisconnectAsync_WhenNotConnected_DoesNotThrow()
    {
        // Act & Assert
        var exception = await Record.ExceptionAsync(
            () => _strategy.DisconnectAsync());

        Assert.Null(exception);
    }
}

internal sealed class RecordingVpnServiceClient : IVpnServiceClient
{
    public List<VpnServiceRequest> Requests { get; } = [];
    public VpnServiceResponse Response { get; set; } = new()
    {
        Success = true,
        OpenVpnState = "Disconnected"
    };

    public Task<VpnServiceResponse> SendAsync(VpnServiceRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Response);
    }

    public Task<bool> IsServiceAvailableAsync(CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}

/// <summary>
/// Tests for OpenVpnSettings load/save lifecycle.
/// </summary>
public class OpenVpnSettingsTests
{
    [Fact]
    public void DefaultSettings_HaveExpectedValues()
    {
        // Arrange & Act
        var settings = new OpenVpnSettings();

        // Assert
        Assert.Null(settings.OpenVpnExePath);
        Assert.Equal(7505, settings.ManagementPort);
        Assert.True(settings.AutoReconnect);
        Assert.Equal(5, settings.MaxReconnectAttempts);
        Assert.Equal([1, 2, 4, 8, 16, 30], settings.ReconnectBackoffSeconds);
    }

    [Fact]
    public void Settings_WithCustomValues_RetainValues()
    {
        // Arrange & Act
        var settings = new OpenVpnSettings
        {
            OpenVpnExePath = @"C:\Custom\openvpn.exe",
            ManagementPort = 9000,
            AutoReconnect = false,
            MaxReconnectAttempts = 3,
            ReconnectBackoffSeconds = [5, 10, 20]
        };

        // Assert
        Assert.Equal(@"C:\Custom\openvpn.exe", settings.OpenVpnExePath);
        Assert.Equal(9000, settings.ManagementPort);
        Assert.False(settings.AutoReconnect);
        Assert.Equal(3, settings.MaxReconnectAttempts);
        Assert.Equal([5, 10, 20], settings.ReconnectBackoffSeconds);
    }

    [Fact]
    public void Load_WhenNoFileExists_ReturnsDefaults()
    {
        // Act
        var settings = OpenVpnSettings.Load();

        // Assert - should return default values without throwing
        Assert.NotNull(settings);
        Assert.Equal(7505, settings.ManagementPort);
        Assert.True(settings.AutoReconnect);
    }
}
