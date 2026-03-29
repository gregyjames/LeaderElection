using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace LeaderElection.FusionCache;

public partial class FusionCacheLeaderElection: LeaderElectionBase<FusionCacheSettings>
{
    private readonly IFusionCache _cache;

    public FusionCacheLeaderElection(IFusionCache cache,
        IOptions<FusionCacheSettings>? options,
        ILogger<FusionCacheLeaderElection> logger): base(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }
    private FusionCacheEntryOptions CreateLockEntryOptions()
    {
        // If a distributed cache is configured, we should ignore memory cache reads/writes
        // to always observe the actual shared lock state and promptly detect leadership loss.
        var useDistributedOnly = _cache.HasDistributedCache;

        var options = new FusionCacheEntryOptions(_settings.LockExpiry)
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

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_settings.LockKey, null, entryOptions, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(currentValue))
                return false;

            await _cache.SetAsync(_settings.LockKey, _settings.InstanceId, entryOptions, cancellationToken).ConfigureAwait(false);

            var verifyValue = await _cache.GetOrDefaultAsync<string?>(_settings.LockKey, null, entryOptions, cancellationToken).ConfigureAwait(false);
            return verifyValue == _settings.InstanceId;
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
            var entryOptions = CreateLockEntryOptions();

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_settings.LockKey, null, entryOptions, cancellationToken).ConfigureAwait(false);
            if (currentValue != _settings.InstanceId)
                return false;

            await _cache.SetAsync(_settings.LockKey, _settings.InstanceId, entryOptions, cancellationToken).ConfigureAwait(false);

            return true;
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
            var entryOptions = CreateLockEntryOptions();

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_settings.LockKey, null, entryOptions, CancellationToken.None).ConfigureAwait(false);
            if (currentValue == _settings.InstanceId)
            {
                await _cache.RemoveAsync(_settings.LockKey, entryOptions, CancellationToken.None).ConfigureAwait(false);
                LogLeadershipReleasedForInstanceInstanceid(_logger, _settings.InstanceId);
            }
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
    static partial void LogLeadershipReleasedForInstanceInstanceid(ILogger logger, string instanceId);

    [LoggerMessage(LogLevel.Error, "Error releasing leadership")]
    static partial void LogErrorReleasingLeadership(ILogger logger, Exception exception);
}
