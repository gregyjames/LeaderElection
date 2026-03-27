using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace LeaderElection.FusionCache;

public class FusionCacheLeaderElection: LeaderElectionBase<FusionCacheSettings>
{
    private readonly IFusionCache _cache;
    private readonly FusionCacheSettings _options;

    public FusionCacheLeaderElection(IFusionCache cache,
        IOptions<FusionCacheSettings> options,
        ILogger<FusionCacheLeaderElection> logger): base(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }
    private FusionCacheEntryOptions CreateLockEntryOptions()
    {
        // If a distributed cache is configured, we should ignore memory cache reads/writes
        // to always observe the actual shared lock state and promptly detect leadership loss.
        var useDistributedOnly = _cache.HasDistributedCache;

        var options = new FusionCacheEntryOptions(_options.LockExpiry)
            .SetFailSafe(false)
            .SetAllowStaleOnReadOnly(false)
            .SetSkipMemoryCacheRead(useDistributedOnly)
            .SetSkipMemoryCacheWrite(useDistributedOnly);

        return options;
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var entryOptions = CreateLockEntryOptions();

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_options.LockKey, default, entryOptions, cancellationToken);
            if (!string.IsNullOrEmpty(currentValue))
                return false;

            await _cache.SetAsync(_options.LockKey, _options.InstanceId, entryOptions, cancellationToken);

            var verifyValue = await _cache.GetOrDefaultAsync<string?>(_options.LockKey, default, entryOptions, cancellationToken);
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
            var entryOptions = CreateLockEntryOptions();

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_options.LockKey, default, entryOptions, cancellationToken);
            if (currentValue != _options.InstanceId)
                return false;

            await _cache.SetAsync(_options.LockKey, _options.InstanceId, entryOptions, cancellationToken);

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
            var entryOptions = CreateLockEntryOptions();

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_options.LockKey, default, entryOptions, CancellationToken.None);
            if (currentValue == _options.InstanceId)
            {
                await _cache.RemoveAsync(_options.LockKey, entryOptions, CancellationToken.None);
                logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing leadership");
        }
    }
}