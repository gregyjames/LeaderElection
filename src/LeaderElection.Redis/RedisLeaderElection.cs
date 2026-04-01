using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LeaderElection.Redis;

/// <summary>
/// Leader Election implementation that uses Redis for leader election. Each contender
/// will attempt to set a "lock key" in Redis with a unique instance ID and an expiration
/// time. The instance that successfully sets the lock key is considered the leader.
/// </summary>
public partial class RedisLeaderElection : LeaderElectionBase<RedisSettings>
{
    private IDatabase? _redis;

    public RedisLeaderElection(
        RedisSettings options,
        ILogger<RedisLeaderElection>? logger = null,
        TimeProvider? timeProvider = null
    )
        : base(options ?? throw new ArgumentNullException(nameof(options)), logger, timeProvider)
    { }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_redis != null)
        {
            LogLockAlreadyAcquired(_settings.LockKey);
            return false;
        }

        var connectionMultiplexer = await GetConnectionMultiplexer(cancellationToken)
            .ConfigureAwait(false);
        var redis = connectionMultiplexer.GetDatabase(_settings.Database);

        var success = await redis
            .StringSetAsync(
                key: _settings.LockKey,
                value: _settings.InstanceId,
                expiry: _settings.LockExpiry,
                when: When.NotExists
            )
            .ConfigureAwait(false);

        if (success)
        {
            _redis = redis;
            LogLockAcquired(_settings.LockKey);
        }
        else
        {
            // Log at debug level since it's expected that multiple contenders will fail to acquire the lock
            LogFailureAcquiringLock(_settings.LockKey);
        }

        return success;
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_redis == null)
        {
            LogNoLockToRenew();
            return false;
        }

        var success = false;
        try
        {
            var script = """
                if redis.call('GET', KEYS[1]) == ARGV[1]
                then
                    return redis.call('PEXPIRE', KEYS[1], ARGV[2])
                else
                    return 0
                end
                """;

            var result = (int)
                await _redis
                    .ScriptEvaluateAsync(
                        script,
                        [_settings.LockKey],
                        [_settings.InstanceId, (int)_settings.LockExpiry.TotalMilliseconds]
                    )
                    .ConfigureAwait(false);

            if (result > 0)
            {
                success = true;
                LogLockRenewed(_settings.LockKey);
            }
            else
            {
                LogFailureRenewingLock(_settings.LockKey);
            }
        }
        finally
        {
            if (!success)
            {
                // give up the lock in our state to avoid being stuck in a bad state
                ForceReset();
            }
        }

        return success;
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (_redis == null)
        {
            LogNoLockToRelease();
            return;
        }

        try
        {
            var script = """
                if redis.call('GET', KEYS[1]) == ARGV[1]
                then
                    return redis.call('DEL', KEYS[1])
                else
                    return 0
                end
                """;

            var keysRemoves = (int)
                await _redis
                    .ScriptEvaluateAsync(script, [_settings.LockKey], [_settings.InstanceId])
                    .ConfigureAwait(false);

            if (keysRemoves > 0)
            {
                LogLockReleased(_settings.LockKey);
            }
            else
            {
                LogFailureReleasingLock(_settings.LockKey);
            }
        }
        finally
        {
            // give up the lock in our state to avoid being stuck in a bad state
            ForceReset();
        }
    }

    private async Task<IConnectionMultiplexer> GetConnectionMultiplexer(CancellationToken ct)
    {
        if (_settings.ConnectionMultiplexerFactory != null)
        {
            if (!string.IsNullOrWhiteSpace(_settings.Host))
            {
                LogIgnoringHostBecauseFactoryIsSet();
            }

            var multiplexer = await _settings
                .ConnectionMultiplexerFactory(_settings, ct)
                .ConfigureAwait(false);
            return multiplexer
                ?? throw new InvalidOperationException(
                    "ConnectionMultiplexerFactory returned null."
                );
        }

        if (!string.IsNullOrWhiteSpace(_settings.Host))
        {
            return await ConnectionMultiplexer
                .ConnectAsync(
                    new ConfigurationOptions
                    {
                        EndPoints = { { _settings.Host, _settings.Port } },
                        Password = _settings.Password,
                        DefaultDatabase = _settings.Database,
                    }
                )
                .ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "Either ConnectionMultiplexerFactory or Host must be specified in settings."
        );
    }

    void ForceReset()
    {
        _redis = null;
    }

    [LoggerMessage(LogLevel.Warning, "Ignoring Host because ConnectionMultiplexerFactory is set.")]
    partial void LogIgnoringHostBecauseFactoryIsSet();

    [LoggerMessage(LogLevel.Debug, "Lock already acquired on {LockKey}.")]
    partial void LogLockAlreadyAcquired(string lockKey);

    [LoggerMessage(LogLevel.Debug, "Lock acquired on {LockKey}.")]
    partial void LogLockAcquired(string lockKey);

    [LoggerMessage(LogLevel.Debug, "Failure acquiring lock on {LockKey}.")]
    partial void LogFailureAcquiringLock(string lockKey);

    [LoggerMessage(LogLevel.Error, "No lock to renew.")]
    partial void LogNoLockToRenew();

    [LoggerMessage(LogLevel.Debug, "Lock renewed on {LockKey}.")]
    partial void LogLockRenewed(string lockKey);

    [LoggerMessage(LogLevel.Information, "Failure renewing lock on {LockKey}.")]
    partial void LogFailureRenewingLock(string lockKey);

    [LoggerMessage(LogLevel.Error, "No lock to release.")]
    partial void LogNoLockToRelease();

    [LoggerMessage(LogLevel.Debug, "Lock released on {LockKey}.")]
    partial void LogLockReleased(string lockKey);

    [LoggerMessage(LogLevel.Information, "Failure releasing lock on {LockKey}.")]
    partial void LogFailureReleasingLock(string lockKey);
}
