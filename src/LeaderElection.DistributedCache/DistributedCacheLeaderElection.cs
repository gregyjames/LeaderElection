using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaderElection.DistributedCache;

[SuppressMessage("Design", "CA1031:Do not catch general exception types")]
public partial class DistributedCacheLeaderElection: LeaderElectionBase<DistributedCacheSettings>
{
    private readonly IDistributedCache _cache;

    public DistributedCacheLeaderElection(IDistributedCache cache,
        IOptions<DistributedCacheSettings>? options,
        ILogger<DistributedCacheLeaderElection> logger): base(options?.Value ?? throw new InvalidOperationException(), logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }
    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentValue = await _cache.GetStringAsync(_settings.LockKey, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(currentValue)) return false;

            await _cache.SetStringAsync(_settings.LockKey, _settings.InstanceId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _settings.LockExpiry
            }, cancellationToken).ConfigureAwait(false);

            var verifyValue = await _cache.GetStringAsync(_settings.LockKey, cancellationToken).ConfigureAwait(false);
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
            var currentValue = await _cache.GetStringAsync(_settings.LockKey, cancellationToken).ConfigureAwait(false);
            if (currentValue != _settings.InstanceId) return false;

            await _cache.SetStringAsync(_settings.LockKey, _settings.InstanceId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _settings.LockExpiry
            }, cancellationToken).ConfigureAwait(false);

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
            var currentValue = await _cache.GetStringAsync(_settings.LockKey).ConfigureAwait(false);
            if (currentValue == _settings.InstanceId)
            {
                await _cache.RemoveAsync(_settings.LockKey).ConfigureAwait(false);
                LogLeadershipReleasedForInstanceInstanceId(_logger, _settings.InstanceId);
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
    static partial void LogLeadershipReleasedForInstanceInstanceId(ILogger logger, string instanceId);

    [LoggerMessage(LogLevel.Error, "Error releasing leadership")]
    static partial void LogErrorReleasingLeadership(ILogger logger, Exception exception);
}
