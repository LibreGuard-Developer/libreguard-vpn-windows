using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using LibreGuard_VPN_Desktop.Models.Api;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Securely stores VPN configuration files and credentials on disk.
/// Private keys and passphrases are encrypted with Windows DPAPI (ProtectedData).
/// Config files are stored under %LocalAppData%\LibreGuardVPN\ with restrictive ACLs.
/// </summary>
internal sealed class VpnConfigStorageService
{
    private readonly DeviceKeyService _deviceKeyService;

    private static readonly string ConfigDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibreGuardVPN");

    public VpnConfigStorageService(DeviceKeyService deviceKeyService)
    {
        ArgumentNullException.ThrowIfNull(deviceKeyService);
        _deviceKeyService = deviceKeyService;
    }

    /// <summary>
    /// Saves a VPN configuration to disk with DPAPI-encrypted credentials.
    /// Returns the path to the main config file.
    /// </summary>
    public string SaveConfig(VpnConfigResponse config)
    {
        ArgumentNullException.ThrowIfNull(config);

        EnsureSecureDirectory();

        var extension = string.Equals(config.Protocol, "IKEV2", StringComparison.OrdinalIgnoreCase)
            ? ".sswan"
            : ".ovpn";
        var configFileName = $"{config.CertificateName}{extension}";
        var configPath = Path.Combine(ConfigDirectory, configFileName);

        // Write the config content (the .ovpn or .sswan content)
        File.WriteAllText(configPath, config.ConfigContent, Encoding.UTF8);

        var passphrase = ResolvePassphrase(config);

        // Encrypt and store the passphrase separately if present
        if (!string.IsNullOrEmpty(passphrase))
        {
            var passphrasePath = Path.Combine(ConfigDirectory, $"{config.CertificateName}.passphrase.dpapi");
            var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
            var encrypted = ProtectedData.Protect(passphraseBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(passphrasePath, encrypted);

            // Zero out the plaintext bytes
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }

        return configPath;
    }

    private string? ResolvePassphrase(VpnConfigResponse config)
    {
        if (!string.IsNullOrEmpty(config.Passphrase))
            return config.Passphrase;

        if (config.EncryptedPassphrase is not null)
            return _deviceKeyService.DecryptPassphrase(config.EncryptedPassphrase);

        return null;
    }

    /// <summary>
    /// Reads the DPAPI-encrypted passphrase for a certificate name.
    /// Returns null if no passphrase was stored.
    /// </summary>
    public string? LoadPassphrase(string certificateName)
    {
        ArgumentNullException.ThrowIfNull(certificateName);

        var passphrasePath = Path.Combine(ConfigDirectory, $"{certificateName}.passphrase.dpapi");
        if (!File.Exists(passphrasePath))
            return null;

        var encrypted = File.ReadAllBytes(passphrasePath);
        var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        var passphrase = Encoding.UTF8.GetString(decrypted);

        CryptographicOperations.ZeroMemory(decrypted);

        return passphrase;
    }

    /// <summary>
    /// Returns the path to a stored config file, or null if it does not exist.
    /// Checks for both .ovpn (OpenVPN) and .sswan (IKEv2/StrongSwan) extensions.
    /// </summary>
    public string? GetConfigPath(string certificateName)
    {
        ArgumentNullException.ThrowIfNull(certificateName);

        var ovpnPath = Path.Combine(ConfigDirectory, $"{certificateName}.ovpn");
        if (File.Exists(ovpnPath))
            return ovpnPath;

        var sswanPath = Path.Combine(ConfigDirectory, $"{certificateName}.sswan");
        return File.Exists(sswanPath) ? sswanPath : null;
    }

    /// <summary>
    /// Removes all stored credentials and config files for a specific certificate.
    /// </summary>
    public void DeleteConfig(string certificateName)
    {
        ArgumentNullException.ThrowIfNull(certificateName);

        TryDeleteFile(Path.Combine(ConfigDirectory, $"{certificateName}.ovpn"));
        TryDeleteFile(Path.Combine(ConfigDirectory, $"{certificateName}.sswan"));
        TryDeleteFile(Path.Combine(ConfigDirectory, $"{certificateName}.passphrase.dpapi"));
    }

    /// <summary>
    /// Removes all stored VPN configuration files and credentials.
    /// Should be called on user logout.
    /// </summary>
    public void PurgeAll()
    {
        if (!Directory.Exists(ConfigDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(ConfigDirectory))
        {
            TryDeleteFile(file);
        }
    }

    /// <summary>
    /// Ensures the config directory exists with ACLs restricted to the current user.
    /// </summary>
    private static void EnsureSecureDirectory()
    {
        if (Directory.Exists(ConfigDirectory))
            return;

        var dirInfo = Directory.CreateDirectory(ConfigDirectory);

        try
        {
            // Restrict access to the current user only
            var security = dirInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                // Remove inherited rules
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    security.RemoveAccessRule(rule);
                }

                // Grant full control only to the current user
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                dirInfo.SetAccessControl(security);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // ACL modification may fail in some environments; directory still created.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // File may be locked by the VPN process; best-effort cleanup.
        }
    }
}
