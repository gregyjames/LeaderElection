using System.ComponentModel.DataAnnotations;

namespace LeaderElection;

public class LeaderElectionSettingsBase
{
    /// <summary>
    /// A unique identifier for the instance participating in the
    /// leader election. It must be unique across all contenders.
    /// <para/>
    /// Must be non-empty.
    /// Default is a new GUID.
    /// </summary>
    [Required]
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The interval at which non-leader instances will attempt to acquire leadership.
    /// <para/>
    /// Must be greater than or equal to zero.
    /// Default is 5 seconds.
    /// </summary>
    /// <remarks>
    /// Unexpected errors during leader acquisition will exponentially increase this interval by
    /// the retry backoff factor. The computed retry interval will be randomly adjusted by ± the
    /// jitter percentage, up to the maximum retry interval. The exact formula is:
    /// <code>
    /// retry := min(RetryInterval * (RetryBackoffFactor ^ errorCount) * (1 ± rand(RetryJitter)), MaxRetryInterval)
    /// </code>
    /// </remarks>
    [CustomValidation(
        typeof(BaseSettingsValidator),
        nameof(BaseSettingsValidator.ValidateRetryInterval)
    )]
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The backoff factor to apply to the retry interval computation.
    /// <para/>
    /// Must be between 1.0 and 3.0.
    /// Default is 2.0.
    /// </summary>
    [Range(1.0, 3.0)]
    public double RetryBackoffFactor { get; set; } = 2.0;

    /// <summary>
    /// The jitter percentage to apply to the retry interval to avoid thundering herd when multiple
    /// contenders are trying to acquire leadership. The actual retry interval will be randomly
    /// adjusted by ± this percentage.
    /// <para/>
    /// Must be between 0.0 and 1.0.
    /// Default is 0.10 (±10%).
    /// </summary>
    [Range(0.0, 1.0)]
    public double RetryJitter { get; set; } = 0.10;

    /// <summary>
    /// The maximum retry interval (after applying the backoff factor and jitter) when acquiring leadership.
    /// <para/>
    /// Must be greater than or equal to <see cref="RetryInterval"/>.
    /// Default is 40 seconds.
    /// </summary>
    [CustomValidation(
        typeof(BaseSettingsValidator),
        nameof(BaseSettingsValidator.ValidateMaxRetryInterval)
    )]
    public TimeSpan MaxRetryInterval { get; set; } = TimeSpan.FromSeconds(40);

    /// <summary>
    /// The interval at which the leader will attempt to renew its leadership.
    /// <para/>
    /// Must be greater than or equal to zero.
    /// Default is 10 seconds.
    /// </summary>
    [CustomValidation(
        typeof(BaseSettingsValidator),
        nameof(BaseSettingsValidator.ValidateRenewInterval)
    )]
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The maximum number of retry attempts before giving up on acquiring leadership.
    /// <para/>
    /// Default is 3.
    /// </summary>
    [Obsolete(
        "This setting is no longer used and will be removed in a future version. Use MaxRetryInterval instead."
    )]
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Indicates whether leadership should be relinquished when the instance
    /// is disposed, which typically occurs during application shutdown.
    /// <para/>
    /// Default is true.
    /// </summary>
    public bool EnableGracefulShutdown { get; set; } = true;

    /// <summary>
    /// Copies the common settings from the source to the destination.
    /// </summary>
    protected static void Copy(LeaderElectionSettingsBase src, LeaderElectionSettingsBase dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        dst.InstanceId = src.InstanceId;
        dst.RetryInterval = src.RetryInterval;
        dst.RetryBackoffFactor = src.RetryBackoffFactor;
        dst.RetryJitter = src.RetryJitter;
        dst.MaxRetryInterval = src.MaxRetryInterval;
        dst.RenewInterval = src.RenewInterval;
        dst.EnableGracefulShutdown = src.EnableGracefulShutdown;
#pragma warning disable CS0618 // Type or member is obsolete
        dst.MaxRetryAttempts = src.MaxRetryAttempts;
#pragma warning restore CS0618 // Type or member is obsolete
    }
}
