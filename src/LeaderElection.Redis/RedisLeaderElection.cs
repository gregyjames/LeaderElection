using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace LeaderElection.Redis;

public partial class RedisLeaderElection : LeaderElectionBase<RedisSettings>
{
    private readonly IDatabase _redis;

    public RedisLeaderElection(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<RedisSettings> options,
        ILogger<RedisLeaderElection> logger)
        : base(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _ = connectionMultiplexer ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
        this._redis = connectionMultiplexer.GetDatabase() ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _redis.StringSetAsync(
                key: _settings.LockKey,
                value: _settings.InstanceId,
                expiry: _settings.LockExpiry,
                when: When.NotExists
            ).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            LogErrorAcquiringLeadership(_logger, ex);
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var script = @"
                if redis.call('GET', KEYS[1]) == ARGV[1]
                then
                    return redis.call('PEXPIRE', KEYS[1], ARGV[2])
                else
                    return 0
                end";

            var result = await _redis.ScriptEvaluateAsync(
                script,
                [ _settings.LockKey ],
                [ _settings.InstanceId, (int)_settings.LockExpiry.TotalMilliseconds ]
            ).ConfigureAwait(false);

            return (int)result != 0;
        }
        catch (Exception ex)
        {
            LogErrorRenewingLeadership(_logger, ex);
            return false;
        }
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        try
        {
            var script = @"
                if redis.call('GET', KEYS[1]) == ARGV[1]
                then
                    return redis.call('DEL', KEYS[1])
                else
                    return 0
                end";

            await _redis.ScriptEvaluateAsync(
                script,
                [ _settings.LockKey ],
                [ _settings.InstanceId ]
            ).ConfigureAwait(false);

            LogLeadershipReleasedForInstanceInstanceId(_logger, _settings.InstanceId);
        }
        catch (Exception ex)
        {
            LogErrorReleasingLeadership(_logger, ex);
        }
    }

    [LoggerMessage(LogLevel.Error, "Error acquiring leadership")]
    static partial void LogErrorAcquiringLeadership(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "Error renewing leadership")]
    static partial void LogErrorRenewingLeadership(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Information, "Leadership released for instance {instanceId}")]
    static partial void LogLeadershipReleasedForInstanceInstanceId(ILogger logger, string instanceId);

    [LoggerMessage(LogLevel.Error, "Error releasing leadership")]
    static partial void LogErrorReleasingLeadership(ILogger logger, Exception exception);
}
