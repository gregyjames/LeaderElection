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
    [CustomValidation(
        typeof(BaseSettingsValidator),
        nameof(BaseSettingsValidator.ValidateRetryInterval)
    )]
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);

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
    /// Must be greater than or equal to zero.
    /// Default is 3.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Whether to enable graceful shutdown when losing leadership.
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
        dst.RenewInterval = src.RenewInterval;
        dst.EnableGracefulShutdown = src.EnableGracefulShutdown;
        dst.MaxRetryAttempts = src.MaxRetryAttempts;
    }
}
