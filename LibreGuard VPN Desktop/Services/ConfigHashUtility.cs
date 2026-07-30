using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Utility for generating deterministic hashes of VPN configurations.
/// Used to uniquely identify configs for certificate caching purposes.
/// </summary>
internal static class ConfigHashUtility
{
    /// <summary>
    /// Generates a SHA256 hash of the given config content.
    /// </summary>
    public static string GenerateHash(string configContent)
    {
        ArgumentNullException.ThrowIfNull(configContent);

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(configContent));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a SHA256 hash from a file path.
    /// </summary>
    public static string GenerateHashFromFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            var content = File.ReadAllText(filePath);
            return GenerateHash(content);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigHashUtility] Failed to hash config file: {ex.Message}");
            throw;
        }
    }
}
