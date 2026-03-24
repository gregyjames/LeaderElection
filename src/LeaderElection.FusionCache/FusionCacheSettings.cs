using System.ComponentModel.DataAnnotations;

namespace LeaderElection.FusionCache;

public class FusionCacheSettings : LeaderElectionSettingsBase
{
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
}
