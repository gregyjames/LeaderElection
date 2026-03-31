using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Caching.Distributed;

namespace LeaderElection.DistributedCache;

public class DistributedCacheSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// Factory function to create an instance of IDistributedCache. This allows for
    /// custom cache implementations or retrieval logic. If not set, the default
    /// IDistributedCache registered in the service provider will be used.
    /// </summary>
    public Func<DistributedCacheSettings, IDistributedCache>? CacheFactory { get; set; }

    /// <summary>
    /// The key used to acquire the lock in the distributed cache.
    /// This should be unique to avoid conflicts with other applications
    /// using the same cache.
    /// <para/>
    /// Default is "leader-election-lock".
    /// </summary>
    [Required]
    public string LockKey { get; set; } = "leader-election-lock";

    /// <summary>
    /// The duration for which the lock will be held in the distributed cache.
    /// If the lock is not renewed within this time, it will expire and another
    /// instance can acquire it.
    /// <para/>
    /// Default is 30 seconds.
    /// </summary>
    [CustomValidation(
        typeof(DistributedCacheSettingsValidator),
        nameof(DistributedCacheSettingsValidator.ValidateLockExpiry)
    )]
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Copies the DistributedCache settings from the source to the destination.
    /// </summary>
    public static void Copy(DistributedCacheSettings src, DistributedCacheSettings dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        LeaderElectionSettingsBase.Copy(src, dst);
        dst.LockKey = src.LockKey;
        dst.LockExpiry = src.LockExpiry;
        dst.CacheFactory = src.CacheFactory;
    }
}
