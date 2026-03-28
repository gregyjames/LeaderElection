using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace LeaderElection.Redis;

[SuppressMessage("Design", "CA1031:Do not catch general exception types")]
public partial class RedisLeaderElection(
    IConnectionMultiplexer connectionMultiplexer,
    IOptions<RedisSettings>? options,
    ILogger<RedisLeaderElection> logger) : LeaderElectionBase<RedisSettings>(options.Value ?? throw new ArgumentNullException(nameof(options)), logger)
{
    private readonly IDatabase _redis = connectionMultiplexer.GetDatabase() ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
    private readonly RedisSettings _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _redis.StringSetAsync(
                key: _options.LockKey,
                value: _options.InstanceId,
                expiry: _options.LockExpiry,
                when: When.NotExists
            ).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            LogErrorAcquiringLeadership(Logger, ex);
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
                [ _options.LockKey ],
                [ _options.InstanceId, (int)_options.LockExpiry.TotalMilliseconds ]
            ).ConfigureAwait(false);

            return (int)result != 0;
        }
        catch (Exception ex)
        {
            LogErrorRenewingLeadership(Logger, ex);
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
                [ _options.LockKey ],
                [ _options.InstanceId ]
            ).ConfigureAwait(false);

            LogLeadershipReleasedForInstanceInstanceId(Logger, _options.InstanceId);
        }
        catch (Exception ex)
        {
            LogErrorReleasingLeadership(Logger, ex);
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


