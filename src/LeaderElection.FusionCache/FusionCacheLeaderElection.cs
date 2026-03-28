using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace LeaderElection.FusionCache;

[SuppressMessage("Design", "CA1031:Do not catch general exception types")]
public partial class FusionCacheLeaderElection: LeaderElectionBase<FusionCacheSettings>
{
    private readonly IFusionCache _cache;
    private readonly FusionCacheSettings _options;

    public FusionCacheLeaderElection(IFusionCache cache,
        IOptions<FusionCacheSettings>? options,
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

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_options.LockKey, null, entryOptions, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(currentValue))
                return false;

            await _cache.SetAsync(_options.LockKey, _options.InstanceId, entryOptions, cancellationToken).ConfigureAwait(false);

            var verifyValue = await _cache.GetOrDefaultAsync<string?>(_options.LockKey, null, entryOptions, cancellationToken).ConfigureAwait(false);
            return verifyValue == _options.InstanceId;
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
            var entryOptions = CreateLockEntryOptions();

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_options.LockKey, null, entryOptions, cancellationToken).ConfigureAwait(false);
            if (currentValue != _options.InstanceId)
                return false;

            await _cache.SetAsync(_options.LockKey, _options.InstanceId, entryOptions, cancellationToken).ConfigureAwait(false);

            return true;
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
            var entryOptions = CreateLockEntryOptions();

            var currentValue = await _cache.GetOrDefaultAsync<string?>(_options.LockKey, null, entryOptions, CancellationToken.None).ConfigureAwait(false);
            if (currentValue == _options.InstanceId)
            {
                await _cache.RemoveAsync(_options.LockKey, entryOptions, CancellationToken.None).ConfigureAwait(false);
                LogLeadershipReleasedForInstanceInstanceid(Logger, _options.InstanceId);
            }
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
    static partial void LogLeadershipReleasedForInstanceInstanceid(ILogger logger, string instanceId);

    [LoggerMessage(LogLevel.Error, "Error releasing leadership")]
    static partial void LogErrorReleasingLeadership(ILogger logger, Exception exception);
}
