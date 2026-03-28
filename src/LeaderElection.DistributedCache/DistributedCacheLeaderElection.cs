using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaderElection.DistributedCache;

[SuppressMessage("Design", "CA1031:Do not catch general exception types")]
public partial class DistributedCacheLeaderElection: LeaderElectionBase<DistributedCacheSettings>
{
    private readonly IDistributedCache _cache;
    private readonly DistributedCacheSettings _options;

    public DistributedCacheLeaderElection(IDistributedCache cache,
        IOptions<DistributedCacheSettings>? options,
        ILogger<DistributedCacheLeaderElection> logger): base(options?.Value ?? throw new InvalidOperationException(), logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }
    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(currentValue)) return false;

            await _cache.SetStringAsync(_options.LockKey, _options.InstanceId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _options.LockExpiry
            }, cancellationToken).ConfigureAwait(false);

            var verifyValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken).ConfigureAwait(false);
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
            var currentValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken).ConfigureAwait(false);
            if (currentValue != _options.InstanceId) return false;

            await _cache.SetStringAsync(_options.LockKey, _options.InstanceId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _options.LockExpiry
            }, cancellationToken).ConfigureAwait(false);

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
            var currentValue = await _cache.GetStringAsync(_options.LockKey).ConfigureAwait(false);
            if (currentValue == _options.InstanceId)
            {
                await _cache.RemoveAsync(_options.LockKey).ConfigureAwait(false);
                LogLeadershipReleasedForInstanceInstanceId(Logger, _options.InstanceId);
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
    static partial void LogLeadershipReleasedForInstanceInstanceId(ILogger logger, string instanceId);

    [LoggerMessage(LogLevel.Error, "Error releasing leadership")]
    static partial void LogErrorReleasingLeadership(ILogger logger, Exception exception);
}

