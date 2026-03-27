using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaderElection.DistributedCache;

public class DistributedCacheLeaderElection: LeaderElectionBase<DistributedCacheSettings>
{
    private readonly IDistributedCache _cache;
    private readonly DistributedCacheSettings _options;

    public DistributedCacheLeaderElection(IDistributedCache cache,
        IOptions<DistributedCacheSettings> options,
        ILogger<DistributedCacheLeaderElection> logger): base(options.Value, logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }
    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken);
            if (!string.IsNullOrEmpty(currentValue)) return false;

            await _cache.SetStringAsync(_options.LockKey, _options.InstanceId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _options.LockExpiry
            }, cancellationToken);

            var verifyValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken);
            return verifyValue == _options.InstanceId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring leadership");
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken);
            if (currentValue != _options.InstanceId) return false;

            await _cache.SetStringAsync(_options.LockKey, _options.InstanceId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _options.LockExpiry
            }, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error renewing leadership");
            return false;
        }
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        try
        {
            var currentValue = await _cache.GetStringAsync(_options.LockKey);
            if (currentValue == _options.InstanceId)
            {
                await _cache.RemoveAsync(_options.LockKey);
                logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing leadership");
        }
    }
}

