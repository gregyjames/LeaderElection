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
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private IDatabase? _redis;

    public RedisLeaderElection(
        RedisSettings options,
        IConnectionMultiplexer connectionMultiplexer,
        ILogger<RedisLeaderElection>? logger = null,
        TimeProvider? timeProvider = null
    )
        : base(options ?? throw new ArgumentNullException(nameof(options)), logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        _connectionMultiplexer = connectionMultiplexer;
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_redis != null)
        {
            LogLockAlreadyAcquired(_settings.LockKey);
            return false;
        }

        var redis = _connectionMultiplexer.GetDatabase(_settings.Database);

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

            var keysRemoved = (int)
                await _redis
                    .ScriptEvaluateAsync(script, [_settings.LockKey], [_settings.InstanceId])
                    .ConfigureAwait(false);

            if (keysRemoved > 0)
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

    void ForceReset()
    {
        _redis = null;
    }

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

    // this is unexpected since we should have the lock. Log as a warning.
    [LoggerMessage(LogLevel.Warning, "Failure renewing lock on {LockKey}.")]
    partial void LogFailureRenewingLock(string lockKey);

    [LoggerMessage(LogLevel.Error, "No lock to release.")]
    partial void LogNoLockToRelease();

    [LoggerMessage(LogLevel.Debug, "Lock released on {LockKey}.")]
    partial void LogLockReleased(string lockKey);

    // this is unexpected since we should have the lock. Log as a warning.
    [LoggerMessage(LogLevel.Warning, "Failure releasing lock on {LockKey}.")]
    partial void LogFailureReleasingLock(string lockKey);
}
