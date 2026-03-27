using System.ComponentModel.DataAnnotations;

namespace LeaderElection.Redis;

public class RedisSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// The Redis server host name or IP address.
    /// <para/>
    /// Default is "localhost".
    /// </summary>
    [Required]
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// The Redis server port number.
    /// <para/>
    /// Default is 6379.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 6379;

    /// <summary>
    /// The password for authenticating with the Redis server, if required.
    /// <para/>
    /// Default is an empty string, which means no password.
    /// </summary>
    [Required(AllowEmptyStrings = true)]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The Redis database number.
    /// <para/>
    /// Default is 0.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int Database { get; set; } = 0;

    /// <summary>
    /// The key used for acquiring the leader lock in Redis.
    /// <para/>
    /// Default is "leader-election-lock".
    /// </summary>
    [Required]
    public string LockKey { get; set; } = "leader-election-lock";

    /// <summary>
    /// The duration for which the leader lock will be held in Redis before it expires.
    /// <para/>
    /// This should be set to a value that is long enough to allow the leader to
    /// perform its duties, but short enough to allow for quick failover in case
    /// the leader goes down.
    /// Default is 30 seconds.
    /// </summary>
    [CustomValidation(
        typeof(RedisSettingsValidator),
        nameof(RedisSettingsValidator.ValidateLockExpiry)
    )]
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);
}
