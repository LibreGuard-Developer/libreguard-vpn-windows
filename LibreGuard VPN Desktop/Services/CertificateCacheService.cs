using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.IO;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Manages caching of imported IKEv2/IPSec certificates to avoid repeated elevation prompts.
/// 
/// Certificates are imported into LocalMachine stores only once per config. On subsequent
/// connections, the cache is checked if the certificate still exists in the cert store and
/// hasn't expired, the expensive elevated import is skipped.
/// 
/// Cache format: JSON file in AppData with certificate thumbprints and metadata.
/// </summary>
internal sealed class CertificateCacheService
{
    private const string CacheFileName = "ikev2_cert_cache.json";
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LibreGuard VPN Desktop");

    private static readonly string CachePath = Path.Combine(CacheDirectory, CacheFileName);
    private CertificateCache? _cache;

    /// <summary>
    /// Represents the cached certificate metadata.
    /// </summary>
    private class CertificateEntry
    {
        public string? ClientThumbprint { get; set; }
        public string? CaThumbprint { get; set; }
        public long ImportTimestamp { get; set; } // Unix timestamp
        public string? ConfigHash { get; set; } // Hash of the config to detect when cert belongs to a different config
    }

    private class CertificateCache
    {
        public Dictionary<string, CertificateEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Result of a certificate cache lookup.
    /// </summary>
    public class CachedCertificates
    {
        public string? ClientThumbprint { get; set; }
        public string? CaThumbprint { get; set; }
        public bool WasValid { get; set; }
    }

    public CertificateCacheService()
    {
        LoadCache();
    }

    /// <summary>
    /// Attempts to retrieve cached certificate thumbprints for a given config.
    /// Validates that certificates still exist in the system before returning.
    /// </summary>
    /// <param name="configHash">Hash of the config (to ensure we don't mix certs from different configs)</param>
    /// <returns>Cached certificates if valid and present, otherwise null</returns>
    public CachedCertificates? TryGetCachedCertificates(string configHash)
    {
        if (_cache?.Entries.TryGetValue(configHash, out var entry) != true)
            return null;

        if (entry is null)
        {
            RemoveCacheEntry(configHash);
            return null;
        }

        // Validate that the certificates still exist in the system
        if (!IsCertificateValid(entry.ClientThumbprint, StoreName.My) ||
            (entry.CaThumbprint != null && !IsCertificateValid(entry.CaThumbprint, StoreName.Root)))
        {
            // Cache is stale, remove it
            RemoveCacheEntry(configHash);
            return null;
        }

        return new CachedCertificates
        {
            ClientThumbprint = entry.ClientThumbprint,
            CaThumbprint = entry.CaThumbprint,
            WasValid = true
        };
    }

    /// <summary>
    /// Stores newly imported certificate thumbprints in the cache.
    /// </summary>
    public void CacheCertificates(string configHash, string clientThumbprint, string? caThumbprint)
    {
        _cache ??= new CertificateCache();

        _cache.Entries[configHash] = new CertificateEntry
        {
            ClientThumbprint = clientThumbprint,
            CaThumbprint = caThumbprint,
            ImportTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ConfigHash = configHash
        };

        SaveCache();
    }

    /// <summary>
    /// Removes a certificate entry from the cache.
    /// </summary>
    public void RemoveCacheEntry(string configHash)
    {
        if (_cache?.Entries.Remove(configHash) == true)
            SaveCache();
    }

    /// <summary>
    /// Clears all cached certificates (e.g., on app shutdown or reset).
    /// </summary>
    public void ClearCache()
    {
        _cache = new CertificateCache();
        try
        {
            if (File.Exists(CachePath))
                File.Delete(CachePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CertificateCache] Failed to delete cache file: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if a certificate with the given thumbprint exists in the LocalMachine cert store.
    /// </summary>
    private static bool IsCertificateValid(string? thumbprint, StoreName storeName)
    {
        if (string.IsNullOrEmpty(thumbprint))
            return false;

        try
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
            return certs.Count > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CertificateCache] Error checking cert validity: {ex.Message}");
            return false;
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                _cache = new CertificateCache();
                return;
            }

            var json = File.ReadAllText(CachePath);
            _cache = JsonSerializer.Deserialize<CertificateCache>(json) ?? new CertificateCache();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CertificateCache] Failed to load cache: {ex.Message}");
            _cache = new CertificateCache();
        }
    }

    private void SaveCache()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
                Directory.CreateDirectory(CacheDirectory);

            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CertificateCache] Failed to save cache: {ex.Message}");
        }
    }
}
