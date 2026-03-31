using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;

namespace LeaderElection.DistributedCache;

public partial class DistributedCacheLeaderElection : LeaderElectionBase<DistributedCacheSettings>
{
    private readonly IDistributedCache _cache;
    private readonly ISystemClock _systemClock;
    private DateTimeOffset? _lockOwnedUntil;

    [MemberNotNullWhen(true, nameof(_lockOwnedUntil))]
    private bool IsLockOwner =>
        _lockOwnedUntil.HasValue && _systemClock.UtcNow < _lockOwnedUntil.Value;

    public DistributedCacheLeaderElection(
        DistributedCacheSettings settings,
        ILogger<DistributedCacheLeaderElection> logger,
        ISystemClock? systemClock = null
    )
        : base(settings ?? throw new ArgumentNullException(nameof(settings)), logger)
    {
        _ =
            settings.CacheFactory
            ?? throw new ArgumentException("CacheFactory must be provided.", nameof(settings));

        _cache =
            settings.CacheFactory.Invoke(settings)
            ?? throw new InvalidOperationException("CacheFactory returned null.");

        _systemClock = systemClock ?? new SystemClock();
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (IsLockOwner)
        {
            LogLockAlreadyAcquired(_settings.LockKey);
            return true;
        }

        try
        {
            var (updatedKey, currentOwner, expiresAt) = await TryTakeOwnershipAsync(
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (!updatedKey && currentOwner == _settings.InstanceId)
            {
                // Apparently we already own the lock. Treat this as no-owner.
                currentOwner = null;
            }

            if (string.IsNullOrEmpty(currentOwner))
            {
                // No owner. Take the lock...
                expiresAt = await TakeOwnershipAsync(cancellationToken).ConfigureAwait(false);
                updatedKey = true;
            }

            if (updatedKey)
            {
                // Because we are using a distributed cache with a non-atomic read-modify-write
                // operation to acquire the lock, there's a possibility that another instance
                // could have updated the key concurrently. To mitigate this, we'll do a quick
                // read after setting the lock to verify that we still hold it.
                currentOwner = await GetOwnershipAsync(cancellationToken).ConfigureAwait(false);
                updatedKey = currentOwner == _settings.InstanceId;
            }

            if (updatedKey)
            {
                _lockOwnedUntil = expiresAt;
                LogLockAcquired(_settings.LockKey, _settings.InstanceId);
                return true;
            }
            else
            {
                LogFailureAcquiringLock(
                    LogLevel.Debug,
                    _settings.LockKey,
                    "Locked by another instance."
                );
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailureAcquiringLock(LogLevel.Warning, _settings.LockKey, ex.Message);
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (!IsLockOwner)
        {
            LogNoActiveLockToRenew(_settings.LockKey);
            return false;
        }

        // We can't perform atomic read-modify-write operations with IDistributedCache,
        // so we're relying on on the fact that the lock key will not be modified outside
        // of this algorithm.
        var success = false;
        try
        {
            var (updatedKey, currentOwner, expiresAt) = await TryTakeOwnershipAsync(
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (currentOwner == _settings.InstanceId && !updatedKey)
            {
                // We still own the lock, so we can renew it by updating the value with
                // the same instance ID
                expiresAt = await TakeOwnershipAsync(cancellationToken).ConfigureAwait(false);
                updatedKey = true;
            }

            if (updatedKey)
            {
                // Because we are using a distributed cache with a non-atomic read-modify-write
                // operation to acquire the lock, there's a possibility that another instance
                // could have updated the key concurrently. To mitigate this, we'll do a quick
                // read after setting the lock to verify that we still hold it.
                currentOwner = await GetOwnershipAsync(cancellationToken).ConfigureAwait(false);
                updatedKey = currentOwner == _settings.InstanceId;
            }

            if (updatedKey)
            {
                success = true;
                _lockOwnedUntil = expiresAt;
                LogLockRenewed(_settings.LockKey, _settings.InstanceId);
            }
            else
            {
                LogFailureRenewingLock(
                    LogLevel.Warning,
                    _settings.LockKey,
                    "Lock lost to another instance."
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailureRenewingLock(LogLevel.Warning, _settings.LockKey, ex.Message);
        }
        finally
        {
            if (!success)
            {
                // Abandon the lock locally if we failed to renew it for any reason.
                _lockOwnedUntil = null;
            }
        }
        return success;
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (!IsLockOwner)
        {
            LogNoActiveLockToRelease(_settings.LockKey);
            return;
        }

        try
        {
            // Only remove the lock if we still own it...
            var currentOwner = await GetOwnershipAsync(CancellationToken.None)
                .ConfigureAwait(false);

            if (currentOwner == _settings.InstanceId)
            {
                // We can't perform atomic read-modify-write operations on the lock, so if we're
                // too close to the lock expiry, we'll just abandon it, allowing it to
                // expire naturally instead of risking a race condition with another instance.
                // This also avoids unnecessary cache operations when we're about to lose the
                // lock anyway.
                var quietPeriod = TimeSpan.FromMilliseconds(50);
                if (_systemClock.UtcNow + quietPeriod < _lockOwnedUntil)
                {
                    await ReleaseOwnershipAsync().ConfigureAwait(false);
                }

                LogLockReleased(_settings.LockKey, _settings.InstanceId);
            }
            else if (string.IsNullOrEmpty(currentOwner))
            {
                LogFailureReleasingLock(
                    LogLevel.Information,
                    _settings.LockKey,
                    "Lock already expired."
                );
            }
            else
            {
                LogFailureReleasingLock(
                    LogLevel.Information,
                    _settings.LockKey,
                    "Lock lost to another instance."
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailureReleasingLock(LogLevel.Warning, _settings.LockKey, ex.Message);
        }
        finally
        {
            // Regardless of whether the release succeeded, we should consider the lock abandoned locally
            _lockOwnedUntil = null;
        }
    }

    private Task<string?> GetOwnershipAsync(CancellationToken cancellationToken) =>
        _cache.GetStringAsync(_settings.LockKey, cancellationToken);

    private async Task<(
        bool updatedKey,
        string? currentOwner,
        DateTimeOffset expiresAt
    )> TryTakeOwnershipAsync(CancellationToken cancellationToken)
    {
        var updatedKey = false;
        var expiresAt = default(DateTimeOffset);
        var currentOwner = await GetOwnershipAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(currentOwner))
        {
            expiresAt = await TakeOwnershipAsync(cancellationToken).ConfigureAwait(false);
            currentOwner = _settings.InstanceId;
            updatedKey = true;
        }
        return (updatedKey, currentOwner, expiresAt);
    }

    private async Task<DateTimeOffset> TakeOwnershipAsync(CancellationToken cancellationToken)
    {
        var expiresAt = _systemClock.UtcNow + _settings.LockExpiry;
        await _cache
            .SetStringAsync(
                _settings.LockKey,
                _settings.InstanceId,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _settings.LockExpiry,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return expiresAt;
    }

    private Task ReleaseOwnershipAsync() =>
        _cache.RemoveAsync(_settings.LockKey, CancellationToken.None);

    [LoggerMessage(LogLevel.Information, "Lock already acquired for key {LockKey}.")]
    partial void LogLockAlreadyAcquired(string lockKey);

    [LoggerMessage(LogLevel.Debug, "Acquired lock for key {LockKey} by instance {InstanceId}.")]
    partial void LogLockAcquired(string lockKey, string instanceId);

    [LoggerMessage("Failure acquiring lock for key {LockKey}: {Reason}")]
    partial void LogFailureAcquiringLock(LogLevel logLevel, string lockKey, string reason);

    [LoggerMessage(LogLevel.Information, "No active lock to renew for key {LockKey}.")]
    partial void LogNoActiveLockToRenew(string lockKey);

    [LoggerMessage(LogLevel.Debug, "Lock renewed for key {LockKey} by instance {InstanceId}.")]
    partial void LogLockRenewed(string lockKey, string instanceId);

    [LoggerMessage("Failure renewing lock for key {LockKey}: {Reason}")]
    partial void LogFailureRenewingLock(LogLevel logLevel, string lockKey, string reason);

    [LoggerMessage(LogLevel.Information, "No active lock to release for key {LockKey}.")]
    partial void LogNoActiveLockToRelease(string lockKey);

    [LoggerMessage(LogLevel.Debug, "Lock released for key {LockKey} by instance {InstanceId}.")]
    partial void LogLockReleased(string lockKey, string instanceId);

    [LoggerMessage("Failure releasing lock for key {LockKey}: {Reason}")]
    partial void LogFailureReleasingLock(LogLevel logLevel, string lockKey, string reason);
}
