using System.ComponentModel.DataAnnotations;
using ZiggyCreatures.Caching.Fusion;

namespace LeaderElection.FusionCache;

public class FusionCacheSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// Factory function to create an instance of IFusionCache. This allows for
    /// custom cache implementations or retrieval logic. If not set, the default
    /// IFusionCache registered in the service provider will be used.
    /// </summary>
    public Func<FusionCacheSettings, IFusionCache>? CacheFactory { get; set; }

    /// <summary>
    /// The key used to acquire the lock in the cache.
    /// This should be unique to avoid conflicts with other locks in the cache.
    /// <para/>
    /// Default is "leader-election-lock".
    /// </summary>
    [Required]
    public string LockKey { get; set; } = "leader-election-lock";

    /// <summary>
    /// The duration for which the lock will be held in the cache before it expires.
    /// This should be set to a value that is long enough to allow the leader to perform
    /// its duties, but not so long that it causes delays in failover if the leader goes down.
    /// <para/>
    /// Default is 30 seconds.
    /// </summary>
    [CustomValidation(
        typeof(FusionCacheSettingsValidator),
        nameof(FusionCacheSettingsValidator.ValidateLockExpiry)
    )]
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Copies the FusionCache settings from the source to the destination.
    /// </summary>
    public static void Copy(FusionCacheSettings src, FusionCacheSettings dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        LeaderElectionSettingsBase.Copy(src, dst);
        dst.LockKey = src.LockKey;
        dst.LockExpiry = src.LockExpiry;
        dst.CacheFactory = src.CacheFactory;
    }
}
