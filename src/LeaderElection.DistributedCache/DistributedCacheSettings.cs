using System.ComponentModel.DataAnnotations;

namespace LeaderElection.DistributedCache;

public class DistributedCacheSettings : LeaderElectionSettingsBase
{
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
}
