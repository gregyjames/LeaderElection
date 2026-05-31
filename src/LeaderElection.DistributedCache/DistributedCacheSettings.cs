using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Caching.Distributed;

namespace LeaderElection.DistributedCache;

/// <summary>
/// Settings for distributed cache-based leader election.
/// </summary>
public class DistributedCacheSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// An optional factory function used to obtain an <see cref="IDistributedCache"/>.
    /// </summary>
    /// <remarks>
    /// If not provided, it will attempt to obtain an <see cref="IDistributedCache"/> from DI
    /// (assuming the leader election is created via DI).
    /// </remarks>
    public Func<DistributedCacheSettings, IDistributedCache>? CacheFactory { get; set; }

    /// <summary>
    /// The key used for acquiring the leader lock.
    /// Default is "leader-election-lock".
    /// </summary>
    /// <remarks>
    /// This should be unique to avoid conflicts with other applications using the same cache.
    /// </remarks>
    [Required]
    public string LockKey { get; set; } = "leader-election-lock";

    /// <summary>
    /// The duration for which the leader lock will be held before it expires.
    /// Default is 30 seconds.
    /// </summary>
    /// <remarks>
    /// This should be set to a value that is long enough to allow the leader to
    /// perform its duties, but short enough to allow for quick failover in case
    /// the leader goes down.
    /// </remarks>
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
        dst.CacheFactory = src.CacheFactory;
        dst.LockKey = src.LockKey;
        dst.LockExpiry = src.LockExpiry;
    }
}
