using System.ComponentModel.DataAnnotations;
using StackExchange.Redis;

namespace LeaderElection.Redis;

/// <summary>
/// Settings for Redis-based leader election.
/// </summary>
public class RedisSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// An optional factory function used to obtain an <see cref="IConnectionMultiplexer"/>.
    /// </summary>
    /// <remarks>
    /// If not provided, a connection will be created using the <see cref="Host"/>,
    /// <see cref="Port"/>, and <see cref="Password"/> properties.
    /// If <see cref="Host"/> is null or empty, it will attempt to obtain an
    /// <see cref="IConnectionMultiplexer"/> from DI (assuming the leader election is created via DI).
    /// </remarks>
    public Func<RedisSettings, IConnectionMultiplexer>? ConnectionMultiplexerFactory { get; set; }

    /// <summary>
    /// The Redis server host name or IP address.
    /// </summary>
    /// <remarks>
    /// Ignored if a <see cref="ConnectionMultiplexerFactory"/> is set.
    /// </remarks>
    public string? Host { get; set; }

    /// <summary>
    /// The Redis server port number. Default is 6379.
    /// </summary>
    /// <remarks>
    /// Ignored if a <see cref="ConnectionMultiplexerFactory"/> is set.
    /// </remarks>
    [Range(1, 65535)]
    public int Port { get; set; } = 6379;

    /// <summary>
    /// The password for authenticating with the Redis server, if required.
    /// </summary>
    /// <remarks>
    /// Ignored if a <see cref="ConnectionMultiplexerFactory"/> is set.
    /// </remarks>
    public string? Password { get; set; }

    /// <summary>
    /// The Redis database number. Default is 0.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int Database { get; set; } = 0;

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
        typeof(RedisSettingsValidator),
        nameof(RedisSettingsValidator.ValidateLockExpiry)
    )]
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Copies the Redis settings from the source to the destination.
    /// </summary>
    public static void Copy(RedisSettings src, RedisSettings dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        LeaderElectionSettingsBase.Copy(src, dst);
        dst.ConnectionMultiplexerFactory = src.ConnectionMultiplexerFactory;
        dst.Host = src.Host;
        dst.Port = src.Port;
        dst.Password = src.Password;
        dst.Database = src.Database;
        dst.LockKey = src.LockKey;
        dst.LockExpiry = src.LockExpiry;
    }
}
