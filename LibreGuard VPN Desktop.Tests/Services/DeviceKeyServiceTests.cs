using System.Security.Cryptography;
using System.Text;
using LibreGuard_VPN_Desktop.Models.Api;
using LibreGuard_VPN_Desktop.Services;

namespace LibreGuard_VPN_Desktop.Tests.Services;

public sealed class DeviceKeyServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lg_device_key_{Guid.NewGuid():N}");

    public DeviceKeyServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void GetRegistration_CreatesStablePublicKeyAndKeyId()
    {
        var keyPath = Path.Combine(_tempDir, "device_key.dpapi");
        var service = new DeviceKeyService(keyPath);

        var first = service.GetRegistration();
        var second = service.GetRegistration();
        var reloaded = new DeviceKeyService(keyPath).GetRegistration();

        Assert.Equal(DeviceKeyService.Algorithm, first.DevicePublicKeyAlgorithm);
        Assert.Equal(first, second);
        Assert.Equal(first, reloaded);

        var publicKeyBytes = Convert.FromBase64String(first.DevicePublicKey);
        var expectedKeyId = Convert.ToHexString(SHA256.HashData(publicKeyBytes)).ToLowerInvariant();
        Assert.Equal(expectedKeyId, first.DevicePublicKeyId);
    }

    [Fact]
    public void DecryptPassphrase_DecryptsBackendShapedPayload()
    {
        var service = new DeviceKeyService(Path.Combine(_tempDir, "device_key.dpapi"));
        var registration = service.GetRegistration();
        const string passphrase = "ikev2-passphrase-123";

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(registration.DevicePublicKey), out _);
        var ciphertext = rsa.Encrypt(Encoding.UTF8.GetBytes(passphrase), RSAEncryptionPadding.OaepSHA256);

        var payload = new EncryptedPassphrasePayload
        {
            Algorithm = DeviceKeyService.Algorithm,
            KeyId = registration.DevicePublicKeyId,
            Ciphertext = Convert.ToBase64String(ciphertext)
        };

        var decrypted = service.DecryptPassphrase(payload);

        Assert.Equal(passphrase, decrypted);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp DPAPI test files.
        }
    }
}
