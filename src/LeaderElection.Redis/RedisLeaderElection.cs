using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace LeaderElection.Redis;

public class RedisLeaderElection(
    IConnectionMultiplexer connectionMultiplexer,
    IOptions<RedisSettings> options,
    ILogger<RedisLeaderElection> logger) : LeaderElectionBase<RedisSettings>(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
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
            );

            return result;
        }
        catch (Exception ex)
        {
            base.logger.LogError(ex, "Error acquiring leadership");
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
            );

            return (int)result != 0;
        }
        catch (Exception ex)
        {
            base.logger.LogError(ex, "Error renewing leadership");
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
            );

            base.logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
        }
        catch (Exception ex)
        {
            base.logger.LogError(ex, "Error releasing leadership");
        }
    }
}


