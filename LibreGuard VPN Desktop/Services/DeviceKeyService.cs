using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LibreGuard_VPN_Desktop.Services;

internal sealed record DeviceKeyRegistration(
    string DevicePublicKey,
    string DevicePublicKeyId,
    string DevicePublicKeyAlgorithm);

/// <summary>
/// Owns the per-Windows-user key pair used by the backend to encrypt VPN passphrases.
/// </summary>
internal sealed class DeviceKeyService
{
    public const string Algorithm = "RSA-OAEP-256";

    private static readonly string DefaultKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LibreGuardVPN",
        "device_key.dpapi");

    private readonly string _keyPath;
    private readonly object _lock = new();
    private byte[]? _privateKeyPkcs8;
    private DeviceKeyRegistration? _registration;

    public DeviceKeyService() : this(DefaultKeyPath)
    {
    }

    internal DeviceKeyService(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        _keyPath = keyPath;
    }

    public DeviceKeyRegistration GetRegistration()
    {
        lock (_lock)
        {
            if (_registration is not null)
                return _registration;

            using var rsa = LoadOrCreateKey();
            var publicKeyBytes = rsa.ExportSubjectPublicKeyInfo();
            var publicKey = Convert.ToBase64String(publicKeyBytes);
            var keyId = Convert.ToHexString(SHA256.HashData(publicKeyBytes)).ToLowerInvariant();

            _registration = new DeviceKeyRegistration(publicKey, keyId, Algorithm);
            return _registration;
        }
    }

    public string DecryptPassphrase(Models.Api.EncryptedPassphrasePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!string.Equals(payload.Algorithm, Algorithm, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported encrypted passphrase algorithm '{payload.Algorithm}'.");

        var registration = GetRegistration();
        if (!string.IsNullOrWhiteSpace(payload.KeyId) &&
            !string.Equals(payload.KeyId, registration.DevicePublicKeyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Encrypted passphrase was issued for a different device key. Please sign in again and retry.");
        }

        var ciphertext = Convert.FromBase64String(payload.Ciphertext);
        using var rsa = LoadPrivateKey();
        var plaintext = rsa.Decrypt(ciphertext, RSAEncryptionPadding.OaepSHA256);

        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private RSA LoadOrCreateKey()
    {
        if (_privateKeyPkcs8 is not null)
            return ImportPrivateKey(_privateKeyPkcs8);

        if (File.Exists(_keyPath))
        {
            var protectedBytes = File.ReadAllBytes(_keyPath);
            _privateKeyPkcs8 = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return ImportPrivateKey(_privateKeyPkcs8);
        }

        using var rsa = RSA.Create(2048);
        _privateKeyPkcs8 = rsa.ExportPkcs8PrivateKey();
        SaveProtectedPrivateKey(_privateKeyPkcs8);
        return ImportPrivateKey(_privateKeyPkcs8);
    }

    private RSA LoadPrivateKey()
    {
        lock (_lock)
        {
            return LoadOrCreateKey();
        }
    }

    private void SaveProtectedPrivateKey(byte[] privateKey)
    {
        var directory = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var protectedBytes = ProtectedData.Protect(privateKey, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_keyPath, protectedBytes);
    }

    private static RSA ImportPrivateKey(byte[] privateKey)
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateKey, out _);
        return rsa;
    }
}
