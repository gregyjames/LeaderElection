using System.ComponentModel.DataAnnotations;
using StackExchange.Redis;

namespace LeaderElection.Redis;

public class RedisSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// The Redis server host name or IP address.
    /// <para/>
    /// Ignored if a <see cref="ConnectionMultiplexerFactory"/> is set.
    /// <para/>
    /// If neither <see cref="ConnectionMultiplexerFactory"/> nor <see cref="Host"/>
    /// are set, the leader election will resolve a ConnectionMultiplexer from the DI container.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// The Redis server port number.
    /// <para/>
    /// Default is 6379.
    /// <para/>
    /// Ignored if a <see cref="ConnectionMultiplexerFactory"/> is set.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 6379;

    /// <summary>
    /// The password for authenticating with the Redis server, if required.
    /// <para/>
    /// Ignored if a <see cref="ConnectionMultiplexerFactory"/> is set.
    /// </summary>
    public string? Password { get; set; }

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

    /// <summary>
    /// An optional factory function to get a Redis connection multiplexer.
    /// <para/>
    /// If set, this factory will be used to get a connection multiplexer
    /// instead of using the Host/Port/Password settings.
    /// <para/>
    /// If neither <see cref="ConnectionMultiplexerFactory"/> nor <see cref="Host"/>
    /// are set, the leader election will resolve a ConnectionMultiplexer from the DI container.
    /// </summary>
    public Func<RedisSettings, IConnectionMultiplexer>? ConnectionMultiplexerFactory { get; set; }

    /// <summary>
    /// Copies the Redis settings from the source to the destination.
    /// </summary>
    public static void Copy(RedisSettings src, RedisSettings dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        LeaderElectionSettingsBase.Copy(src, dst);
        dst.Host = src.Host;
        dst.Port = src.Port;
        dst.Password = src.Password;
        dst.Database = src.Database;
        dst.LockKey = src.LockKey;
        dst.LockExpiry = src.LockExpiry;
        dst.ConnectionMultiplexerFactory = src.ConnectionMultiplexerFactory;
    }
}
