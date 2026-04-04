using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace LeaderElection.FusionCache;

public partial class FusionCacheLeaderElection : LeaderElectionBase<FusionCacheSettings>
{
    private readonly IFusionCache _cache;
    private DateTimeOffset? _lockOwnedUntil;

    [MemberNotNullWhen(true, nameof(_lockOwnedUntil))]
    private bool IsLockOwner =>
        _lockOwnedUntil.HasValue && _timeProvider.GetUtcNow() < _lockOwnedUntil.Value;

    public FusionCacheLeaderElection(
        FusionCacheSettings settings,
        IFusionCache cache,
        ILogger<FusionCacheLeaderElection>? logger = null,
        TimeProvider? timeProvider = null
    )
        : base(settings ?? throw new ArgumentNullException(nameof(settings)), logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
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
            var entryOptions = CreateLockEntryOptions();
            var (updatedKey, currentOwner, expiresAt) = await TryTakeOwnershipAsync(
                    entryOptions,
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
                expiresAt = await TakeOwnershipAsync(entryOptions, cancellationToken)
                    .ConfigureAwait(false);
                updatedKey = true;
            }

            if (updatedKey)
            {
                // Because we are using a distributed cache with a non-atomic read-modify-write
                // operation to acquire the lock, there's a possibility that another instance
                // could have updated the key concurrently. To mitigate this, we'll do a quick
                // read after setting the lock to verify that we still hold it.
                currentOwner = await GetOwnershipAsync(entryOptions, cancellationToken)
                    .ConfigureAwait(false);
                updatedKey = currentOwner == _settings.InstanceId;
            }

            if (updatedKey)
            {
                _lockOwnedUntil = expiresAt;
                LogLockAcquired(_settings.LockKey);
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

        // We can't perform atomic read-modify-write operations with FusionCache,
        // so we're relying on the fact that well-behaved ElectionLeaders will not
        // expiry time.
        var success = false;
        try
        {
            var entryOptions = CreateLockEntryOptions();
            var (updatedKey, currentOwner, expiresAt) = await TryTakeOwnershipAsync(
                    entryOptions,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (currentOwner == _settings.InstanceId && !updatedKey)
            {
                // We still own the lock, so we can renew it by updating the value with
                // the same instance ID
                expiresAt = await TakeOwnershipAsync(entryOptions, cancellationToken)
                    .ConfigureAwait(false);
                updatedKey = true;
            }

            if (updatedKey)
            {
                // Because we are using a distributed cache with a non-atomic read-modify-write
                // operation to acquire the lock, there's a possibility that another instance
                // could have updated the key concurrently. To mitigate this, we'll do a quick
                // read after setting the lock to verify that we still hold it.
                currentOwner = await GetOwnershipAsync(entryOptions, cancellationToken)
                    .ConfigureAwait(false);
                updatedKey = currentOwner == _settings.InstanceId;
            }

            if (updatedKey)
            {
                success = true;
                _lockOwnedUntil = expiresAt;
                LogLockRenewed(_settings.LockKey);
            }
            else
            {
                // this is unexpected since we should own the lock.
                // Log as a warning and give up our leadership.
                LogFailureRenewingLock(
                    LogLevel.Warning,
                    _settings.LockKey,
                    "Lock lost to another instance."
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailureRenewingLock(LogLevel.Error, _settings.LockKey, ex.Message);
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
            var entryOptions = CreateLockEntryOptions(TimeSpan.FromSeconds(1)); // short expiry
            var currentOwner = await GetOwnershipAsync(entryOptions, CancellationToken.None)
                .ConfigureAwait(false);

            if (currentOwner == _settings.InstanceId)
            {
                // We can't perform atomic read-modify-write operations on the lock, so if we're
                // too close to the lock expiry, we'll just abandon it, allowing it to
                // expire naturally instead of risking a race condition with another instance.
                // This also avoids unnecessary cache operations when we're about to lose the
                // lock anyway.
                var quietPeriod = TimeSpan.FromMilliseconds(50);
                if (_timeProvider.GetUtcNow() + quietPeriod < _lockOwnedUntil)
                {
                    await ReleaseOwnershipAsync(entryOptions).ConfigureAwait(false);
                }

                LogLockReleased(_settings.LockKey);
            }
            else
            {
                // this is unexpected since we should own the lock, but since we cant
                // verify ownership atomically, it's possible that another instance
                // has legitimately already taken over the lock.
                // Log as information and give up our leadership.
                LogFailureReleasingLock(
                    LogLevel.Information,
                    _settings.LockKey,
                    string.IsNullOrEmpty(currentOwner)
                        ? "Lock already expired."
                        : "Lock lost to another instance."
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailureReleasingLock(LogLevel.Error, _settings.LockKey, ex.Message);
        }
        finally
        {
            // Regardless of whether the release succeeded, we should consider the lock abandoned locally
            _lockOwnedUntil = null;
        }
    }

    private FusionCacheEntryOptions CreateLockEntryOptions(TimeSpan? durationOverride = null)
    {
        // If a distributed cache is configured, we should ignore memory cache reads/writes
        // to always observe the actual shared lock state and promptly detect leadership loss.
        var useDistributedOnly = _cache.HasDistributedCache;

        var options = new FusionCacheEntryOptions(durationOverride ?? _settings.LockExpiry)
            .SetFailSafe(false)
            .SetAllowStaleOnReadOnly(false)
            .SetSkipMemoryCacheRead(useDistributedOnly)
            .SetSkipMemoryCacheWrite(useDistributedOnly);

        return options;
    }

    private ValueTask<string?> GetOwnershipAsync(
        FusionCacheEntryOptions entryOptions,
        CancellationToken cancellationToken
    ) =>
        _cache.GetOrDefaultAsync<string?>(
            _settings.LockKey,
            null, // default
            entryOptions,
            cancellationToken
        );

    private async Task<(
        bool updatedKey,
        string currentOwner,
        DateTimeOffset expiresAt
    )> TryTakeOwnershipAsync(
        FusionCacheEntryOptions entryOptions,
        CancellationToken cancellationToken
    )
    {
        var updatedKey = false;
        var expiresAt = _timeProvider.GetUtcNow() + entryOptions.Duration;
        var currentOwner = await _cache
            .GetOrSetAsync<string>(
                _settings.LockKey,
                (_, _) =>
                {
                    updatedKey = true;
                    return Task.FromResult(_settings.InstanceId);
                },
                entryOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return (updatedKey, currentOwner, expiresAt);
    }

    private async Task<DateTimeOffset> TakeOwnershipAsync(
        FusionCacheEntryOptions entryOptions,
        CancellationToken cancellationToken
    )
    {
        var expiresAt = _timeProvider.GetUtcNow() + entryOptions.Duration;
        await _cache
            .SetAsync(_settings.LockKey, _settings.InstanceId, entryOptions, cancellationToken)
            .ConfigureAwait(false);
        return expiresAt;
    }

    private ValueTask ReleaseOwnershipAsync(FusionCacheEntryOptions entryOptions) =>
        _cache.RemoveAsync(_settings.LockKey, entryOptions, CancellationToken.None);

    [LoggerMessage(LogLevel.Information, "Lock already acquired for key {LockKey}.")]
    partial void LogLockAlreadyAcquired(string lockKey);

    [LoggerMessage(LogLevel.Debug, "Acquired lock for key {LockKey}.")]
    partial void LogLockAcquired(string lockKey);

    [LoggerMessage("Failure acquiring lock for key {LockKey}: {Reason}")]
    partial void LogFailureAcquiringLock(LogLevel logLevel, string lockKey, string reason);

    [LoggerMessage(LogLevel.Information, "No active lock to renew for key {LockKey}.")]
    partial void LogNoActiveLockToRenew(string lockKey);

    [LoggerMessage(LogLevel.Debug, "Lock renewed for key {LockKey}.")]
    partial void LogLockRenewed(string lockKey);

    [LoggerMessage("Failure renewing lock for key {LockKey}: {Reason}")]
    partial void LogFailureRenewingLock(LogLevel logLevel, string lockKey, string reason);

    [LoggerMessage(LogLevel.Information, "No active lock to release for key {LockKey}.")]
    partial void LogNoActiveLockToRelease(string lockKey);

    [LoggerMessage(LogLevel.Debug, "Lock released for key {LockKey}.")]
    partial void LogLockReleased(string lockKey);

    [LoggerMessage("Failure releasing lock for key {LockKey}: {Reason}")]
    partial void LogFailureReleasingLock(LogLevel logLevel, string lockKey, string reason);
}
