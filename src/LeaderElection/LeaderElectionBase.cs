using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeaderElection;

public abstract partial class LeaderElectionBase<TSettings> : ILeaderElection
    where TSettings : LeaderElectionSettingsBase
{
    private readonly SemaphoreSlim _leaderLoopSemaphore = new(1, 1);
    private volatile Task? _leaderLoopTask;
    private CancellationTokenSource? _leaderLoopTaskCTS;
    private DateTimeOffset _lastLeadershipRenewal;
    private volatile bool _isLeader;
    private volatile int _disposedValue;

    [SuppressMessage("Design", "CA1051", Justification = "Field readonly to derived types")]
    protected readonly TSettings _settings;

    [SuppressMessage("Design", "CA1051", Justification = "Field readonly to derived types")]
    protected readonly ILogger _logger;

    [SuppressMessage("Design", "CA1051", Justification = "Field readonly to derived types")]
    protected readonly TimeProvider _timeProvider;

    protected LeaderElectionBase(
        TSettings settings,
        ILogger? logger = null,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _logger = logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler<LeadershipChangedEventArgs>? LeadershipChanged;

    /// <inheritdoc />
    public event EventHandler<LeadershipExceptionEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    public bool LeaderLoopRunning => _leaderLoopTask != null;

    /// <inheritdoc />
    public bool IsLeader => _isLeader;

    /// <inheritdoc />
    public DateTime LastLeadershipRenewal =>
        _isLeader ? _lastLeadershipRenewal.UtcDateTime : DateTime.MinValue;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (LeaderLoopRunning)
        {
            LogLeaderElectionIsAlreadyRunning(_settings.InstanceId);
            return;
        }

        CancellationTokenSource? cts = null;
        await _leaderLoopSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (LeaderLoopRunning)
            {
                LogLeaderElectionIsAlreadyRunning(_settings.InstanceId);
                return;
            }

            LogStartingLeaderElection(_settings.InstanceId);

            // Important: DO NOT link the Leader Loop Task to the provided cancellation
            // token since we need to control the lifetime of this task independently of
            // external cancellation requests. There is also no reason to think that the
            // provided cancellation token is long-lived -- it could be tied to a short-
            // lived API call, for example.
            // The user must call StopAsync() (or DisposeAsync()) to stop the leader loop.
            cts = new CancellationTokenSource();
            var task = RunLeaderLoopAsync(cts.Token);

            // success
            _leaderLoopTask = task;
            _leaderLoopTaskCTS = cts;
            cts = null; // ownership transferred
        }
        finally
        {
            _leaderLoopSemaphore.Release();
            cts?.Dispose();
        }
    }

    /// <inheritdoc />
    public virtual Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return InternalStopAsync(true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Fast checks before acquiring the semaphore to avoid unnecessary waits
        if (!LeaderLoopRunning)
        {
            throw new InvalidOperationException(
                "Leader loop must be running to acquire leadership"
            );
        }

        if (IsLeader)
        {
            return true; // Already the leader
        }

        Exception? exceptionInfo = null;
        var isLeader = false;
        await _leaderLoopSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!LeaderLoopRunning)
            {
                throw new InvalidOperationException(
                    "Leader loop must be running to acquire leadership"
                );
            }

            if (IsLeader)
            {
                return true; // Already the leader
            }

            if (!await TryAcquireLeadershipInternalAsync(cancellationToken).ConfigureAwait(false))
            {
                LogFailedToAcquireLeadership(_settings.InstanceId);
                return false;
            }

            // Successfully acquired leadership
            _isLeader = true;
            _lastLeadershipRenewal = _timeProvider.GetUtcNow();
            LogLeadershipAcquired(_settings.InstanceId);
        }
        catch (Exception ex)
        {
            // don't throw since this is a "try" method
            LogErrorAcquiringLeadership(ex, _settings.InstanceId);
            exceptionInfo = ex; // capture for event raising after releasing semaphore
        }
        finally
        {
            isLeader = _isLeader;
            _leaderLoopSemaphore.Release();
        }

        // Fire events outside of the lock to avoid potential deadlocks
        // if event handlers interact with the leader election instance
        NotifyLeadershipStatus(false, isLeader, exceptionInfo);
        return isLeader;
    }

    /// <inheritdoc />
    public async Task RunTaskIfLeaderAsync(
        Func<Task> task,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();

        if (IsLeader)
        {
            await task().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task RunTaskIfLeaderAsync(Action task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return RunTaskIfLeaderAsync(
            () =>
            {
                task();
                return Task.CompletedTask;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Tries to acquire leadership. Implementations should return true if leadership was
    /// successfully acquired, or false if leadership could not be acquired (e.g. another
    /// instance holds the leadership).
    /// <para/>
    /// If an unexpected error occurs, implementations should abandon leadership in their
    /// internal state and throw an exception. The leader loop will catch the exception,
    /// log it, and attempt to reacquire leadership on the next iteration. This is a safety
    /// measure to avoid a situation where an instance thinks it is the leader when it is not.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>true if leadership was successfully acquired; otherwise, false.</returns>
    protected abstract Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Tries to renew leadership. Implementations should return true if leadership was
    /// successfully renewed, or false if leadership was lost (e.g. due to a connectivity
    /// issue with the underlying store). If leadership is lost, the leader loop will attempt
    /// to reacquire leadership on the next iteration.
    /// <para/>
    /// If an unexpected error occurs, implementations should abandon leadership in their
    /// internal state and throw an exception. The leader loop will catch the exception,
    /// log it, and attempt to reacquire leadership on the next iteration. This is a safety
    /// measure to avoid a situation where an instance thinks it is the leader when it is not.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>true if leadership was successfully renewed; otherwise, false.</returns>
    protected abstract Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Releases leadership. This will be called when StopAsync() is called while the instance
    /// is the leader. Implementations should perform any necessary cleanup to release
    /// leadership (e.g. delete leader key from the store).
    /// <para/>
    /// If an unexpected error occurs, implementations should abandon leadership in their
    /// internal state and throw an exception. The leader loop will catch the exception and
    /// log it. This is a safety measure to avoid a situation where an instance thinks it is
    /// the leader when it is not.
    /// </summary>
    protected abstract Task ReleaseLeadershipAsync();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposedValue != 0, this);

    private async Task RunLeaderLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var wasLeader = false;
            var isLeader = false;
            Exception? exceptionInfo = null;

            // Grab the semaphore to ensure that we don't get interrupted by
            // TryAcquireLeadershipAsync() or StopAsync().
            await _leaderLoopSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                wasLeader = _isLeader;
                if (!wasLeader)
                {
                    _isLeader = await TryAcquireLeadershipInternalAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (_isLeader)
                    {
                        LogLeadershipAcquired(_settings.InstanceId);
                    }
                    else
                    {
                        // very common that leadership acquisition fails, typically because
                        // another instance holds the leadership
                        LogFailedToAcquireLeadershipWillRetry(_settings.InstanceId);
                    }
                }
                else
                {
                    _isLeader = await RenewLeadershipInternalAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (_isLeader)
                    {
                        // very common that leadership renewal succeeds, but we log it anyway
                        // to provide visibility into the leader loop's operations
                        LogLeadershipRenewedSuccessfully(_settings.InstanceId);
                    }
                    else
                    {
                        // unexpected
                        LogLostLeadershipDuringRenewal(_settings.InstanceId);
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogUnexpectedErrorInLeaderElection(ex, _settings.InstanceId);
                exceptionInfo = ex;

                // On any unexpected error, we assume we are no longer the leader.
                // This is a safety measure to avoid a situation where an instance thinks
                // it is the leader when it is not.
                _isLeader = false;
            }
            finally
            {
                isLeader = _isLeader;
                if (isLeader)
                {
                    _lastLeadershipRenewal = _timeProvider.GetUtcNow();
                }

                _leaderLoopSemaphore.Release();
            }

            // Fire events outside of the lock to avoid potential deadlocks
            // if event handlers interact with the leader election instance
            NotifyLeadershipStatus(wasLeader, isLeader, exceptionInfo);

            // Wait before checking again...
            var delay = GetNextDelay(isLeader ? 0 : 1);
            await _timeProvider.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    protected virtual TimeSpan GetNextDelay(int retryCount) =>
        retryCount == 0 ? _settings.RenewInterval : _settings.RetryInterval;

    private async Task InternalStopAsync(
        bool releaseLeadership,
        CancellationToken cancellationToken = default
    )
    {
        // Fast checks before acquiring the semaphore to avoid unnecessary waits
        if (!LeaderLoopRunning)
        {
            return; // already stopped
        }

        var wasLeader = false;
        var isLeader = false;
        Exception? exceptionInfo = null;

        await _leaderLoopSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!LeaderLoopRunning)
            {
                return; // already stopped
            }

            Debug.Assert(_leaderLoopTask != null);
            Debug.Assert(_leaderLoopTaskCTS != null);

            // First stop the leader loop to prevent any further leadership
            // changes while we are trying to stop

            // Important: We are purposefully ignoring the provided cancellation token
            // since we want to ensure that the leader loop is stopped and that we
            // don't end up in a situation where the leader loop is still running but
            // we consider ourselves not the leader because of a cancellation request.
            await StopLeaderLoopAsync().ConfigureAwait(false);

            // we must not consider ourselves leader since we've abandoned the
            // leader election process
            wasLeader = _isLeader;
            _isLeader = false;

            if (wasLeader && releaseLeadership)
            {
                try
                {
                    await ReleaseLeadershipAsync().ConfigureAwait(false);
                    LogLeadershipReleased(_settings.InstanceId);
                }
                catch (Exception ex)
                {
                    LogErrorReleasingLeadership(ex, _settings.InstanceId);
                    exceptionInfo = ex; // capture for event raising after releasing semaphore
                }
            }
        }
        finally
        {
            isLeader = _isLeader;
            _leaderLoopSemaphore.Release();
        }

        NotifyLeadershipStatus(wasLeader, isLeader, exceptionInfo);

        async Task StopLeaderLoopAsync()
        {
            LogStoppingLeaderElection(_settings.InstanceId);

            // Cancel the leader loop task, wait for it to complete, and cleanup resources...
            try
            {
                await _leaderLoopTaskCTS.CancelAsync().ConfigureAwait(false);
                await _leaderLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected, just proceed with stopping
            }
            catch (Exception ex)
            {
                // Task exited with an unexpected exception. Log it, but proceed
                // with stopping to avoid leaving the system in an inconsistent state.
                LogUnexpectedErrorInLeaderElection(ex, _settings.InstanceId);
            }
            finally
            {
                _leaderLoopTask = null;
                _leaderLoopTaskCTS.Dispose();
                _leaderLoopTaskCTS = null;
            }
        }
    }

    private void NotifyLeadershipStatus(bool wasLeader, bool isLeader, Exception? exceptionInfo)
    {
        Debug.Assert(
            _leaderLoopSemaphore.CurrentCount == 1,
            "Must be called outside of the leader loop lock"
        );

        if (exceptionInfo != null)
        {
            ErrorOccurred?.Invoke(this, new(exceptionInfo));
        }
        if (wasLeader != isLeader)
        {
            LeadershipChanged?.Invoke(this, new(isLeader));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedValue, 1) == 1)
        {
            return; // Already disposed/disposing
        }

        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        try
        {
            await InternalStopAsync(_settings.EnableGracefulShutdown).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorDuringAsyncDisposal(ex);
        }

        _leaderLoopTaskCTS?.Dispose();
        _leaderLoopSemaphore.Dispose();
        _isLeader = false;
    }

    [LoggerMessage(
        LogLevel.Information,
        "Leader election is already running for instance {InstanceId}."
    )]
    partial void LogLeaderElectionIsAlreadyRunning(string instanceId);

    [LoggerMessage(LogLevel.Information, "Starting leader election for instance {InstanceId}.")]
    partial void LogStartingLeaderElection(string instanceId);

    [LoggerMessage(LogLevel.Information, "Stopping leader election for instance {InstanceId}.")]
    partial void LogStoppingLeaderElection(string instanceId);

    // An exception occurred in the leader election process. Log at Error level since this
    // likely indicates an issue with the underlying store or infrastructure.
    [LoggerMessage(
        LogLevel.Error,
        "Unexpected error in leader election for instance {InstanceId}."
    )]
    partial void LogUnexpectedErrorInLeaderElection(Exception errorMessage, string instanceId);

    // TryAcquireLeadershipInternalAsync threw an exception. This is unexpected since
    // implementations should handle expected exceptions. Log at Error level since this
    // likely indicates an issue with the underlying store or infrastructure.
    [LoggerMessage(
        LogLevel.Error,
        "Error while trying to acquire leadership for instance {InstanceId}."
    )]
    partial void LogErrorAcquiringLeadership(Exception errorMessage, string instanceId);

    [LoggerMessage(LogLevel.Information, "Failed to acquire leadership for instance {InstanceId}.")]
    partial void LogFailedToAcquireLeadership(string instanceId);

    [LoggerMessage(LogLevel.Information, "Leadership acquired for instance {InstanceId}.")]
    partial void LogLeadershipAcquired(string instanceId);

    // It is very common that leadership acquisition fails because another instance
    // holds the leadership. Log at debug level.
    [LoggerMessage(
        LogLevel.Debug,
        "Failed to acquire leadership for instance {InstanceId}, will retry."
    )]
    partial void LogFailedToAcquireLeadershipWillRetry(string instanceId);

    // It is very common that leadership renewal succeeds, so log at debug level.
    [LoggerMessage(LogLevel.Debug, "Successfully renewed leadership for instance {InstanceId}.")]
    partial void LogLeadershipRenewedSuccessfully(string instanceId);

    // Losing leadership during renewal is not normal and could indicate a potential
    // issue (e.g. connectivity problems with the underlying store), so we log at
    // Warning level to highlight this. The leader loop will attempt to reacquire
    // leadership on the next iteration, but it is important to log this event since
    // it could indicate a potential problem that needs attention.
    [LoggerMessage(LogLevel.Warning, "Lost leadership for instance {InstanceId} during renewal.")]
    partial void LogLostLeadershipDuringRenewal(string instanceId);

    [LoggerMessage(LogLevel.Information, "Released leadership for instance {InstanceId}.")]
    partial void LogLeadershipReleased(string instanceId);

    // ReleaseLeadershipAsync threw an exception. This is unexpected since implementations
    // should handle expected exceptions. Log at Error level since this likely indicates an
    // issue with the underlying store or infrastructure.
    [LoggerMessage(LogLevel.Error, "Error releasing leadership for instance {InstanceId}.")]
    partial void LogErrorReleasingLeadership(Exception ex, string instanceId);

    // InternalStopAsync threw an exception during disposal. Log at Error level since
    // we want to avoid throwing from DisposeAsync, but we still want to log this since
    // it could indicate a potential issue that needs attention.
    [LoggerMessage(LogLevel.Error, "Error during async disposal.")]
    partial void LogErrorDuringAsyncDisposal(Exception errorMessage);
}
