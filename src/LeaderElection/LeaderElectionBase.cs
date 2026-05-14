using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeaderElection;

public abstract partial class LeaderElectionBase<TSettings> : ILeaderElection
    where TSettings : LeaderElectionSettingsBase
{
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

    private readonly SemaphoreSlim _leadershipSemaphore = new(1, 1);
    private CancellationTokenSource? _leadershipLoopCancellationTokenSource = new();

    private volatile bool _isLeader;
    private int _disposedValue;
    private Task? _leadershipLoopTask;
    private DateTimeOffset _lastLeadershipRenewalTime = DateTimeOffset.MinValue;
    public event EventHandler<LeadershipChangedEventArgs>? LeadershipChanged;
    public event EventHandler<LeadershipExceptionEventArgs>? ErrorOccurred;

    public bool LeaderLoopRunning => Volatile.Read(ref _leadershipLoopTask)?.IsCompleted is false;

    private bool IsDisposed => Volatile.Read(ref _disposedValue) == 1;
    public bool IsLeader => _isLeader && !IsDisposed;
    public DateTime LastLeadershipRenewal => _lastLeadershipRenewalTime.UtcDateTime;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_leadershipLoopTask is { IsCompleted: false })
        {
            LogLeaderElectionIsAlreadyRunning();
            return;
        }

        LogStartingLeaderElectionForInstanceInstanceid(_settings.InstanceId);

        _leadershipLoopCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        _leadershipLoopTask = RunLeaderLoopAsync(_leadershipLoopCancellationTokenSource.Token);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RunLeaderLoopAsync(CancellationToken token)
    {
        ThrowIfDisposed();

        var retryCount = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_isLeader)
                {
                    if (await TryAcquireLeadershipAsync(token).ConfigureAwait(false))
                    {
                        LogLeadershipAcquiredForInstanceInstanceid(_settings.InstanceId);
                        retryCount = 0;
                    }
                    else
                    {
                        // Very common that leadership acquisition fails because another instance holds the leadership - log at debug level in that case and avoid log noise
                        LogFailedToAcquireLeadershipWillRetry();
                        retryCount++;
                    }
                }
                else
                {
                    if (!await RenewLeadershipInternalAsync(token).ConfigureAwait(false))
                    {
                        LogLostLeadershipDuringRenewalForInstanceInstanceid(_settings.InstanceId);
                        SetLeadership(false);
                        retryCount++;
                    }
                    else
                    {
                        LogLeadershipRenewedSuccessfully();
                        _lastLeadershipRenewalTime = _timeProvider.GetUtcNow();
                        retryCount = 0;
                    }
                }

                var delay = GetNextDelay(retryCount);
                await _timeProvider.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogUnexpectedErrorInLeaderLoop(ex);
                ErrorOccurred?.Invoke(this, new(ex));
                retryCount++;

                try
                {
                    await _timeProvider.Delay(_settings.RetryInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    protected virtual TimeSpan GetNextDelay(int retryCount)
    {
        if (retryCount == 0)
        {
            return _settings.RenewInterval;
        }

        return TimeSpan.FromSeconds(
            Math.Min(Math.Pow(2, Math.Min(retryCount, _settings.MaxRetryAttempts)), 60)
        );
    }

    public virtual Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return InternalStopAsync(cancellationToken);
    }

    private async Task InternalStopAsync(CancellationToken cancellationToken = default)
    {
        if (_leadershipLoopCancellationTokenSource != null)
        {
            LogStoppingLeaderElectionForInstanceInstanceid(_settings.InstanceId);

            await _leadershipLoopCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }

        if (_leadershipLoopTask != null)
        {
            try
            {
                await _leadershipLoopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _leadershipLoopTask = null;
                _leadershipLoopCancellationTokenSource?.Dispose();
                _leadershipLoopCancellationTokenSource = null;
            }
        }

        if (_isLeader)
        {
            if (_settings.EnableGracefulShutdown)
            {
                await ReleaseLeadershipAsync().ConfigureAwait(false);
            }
            else
            {
                await ResetLeadershipAsync().ConfigureAwait(false);
            }

            SetLeadership(false);
        }
    }

    protected abstract Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    );

    protected abstract Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken);
    protected abstract Task ReleaseLeadershipAsync();
    protected abstract ValueTask ResetLeadershipAsync();

    protected void SetLeadership(bool isLeader)
    {
        if (_isLeader != isLeader)
        {
            _isLeader = isLeader;
            _lastLeadershipRenewalTime = isLeader
                ? _timeProvider.GetUtcNow()
                : DateTimeOffset.MinValue;
            LeadershipChanged?.Invoke(this, new(isLeader));
        }
    }

    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _leadershipSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var acquired = await TryAcquireLeadershipInternalAsync(cancellationToken)
                .ConfigureAwait(false);
            SetLeadership(acquired);
            return acquired;
        }
        finally
        {
            _leadershipSemaphore.Release();
        }
    }

    public async Task RunTaskIfLeaderAsync(
        Func<Task> task,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();

        if (!IsLeader)
        {
            LogNotTheLeaderSkippingTaskExecution();
            return;
        }

        try
        {
            LogExecutingTaskAsLeader();
            if (task != null)
            {
                await task().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogErrorExecutingLeaderTask(ex);
            ErrorOccurred?.Invoke(this, new(ex));
            throw;
        }
    }

    public async Task RunTaskIfLeaderAsync(
        Action task,
        CancellationToken cancellationToken = default
    )
    {
        await RunTaskIfLeaderAsync(() => Task.Run(task, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedValue, 1) == 1)
            return;

        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        try
        {
            await InternalStopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorDuringAsyncDisposal(ex);
        }

        _leadershipLoopCancellationTokenSource?.Dispose();
        _leadershipSemaphore.Dispose();
    }

    private void ThrowIfDisposed()
    {
#if NET6_0_OR_GREATER
        ObjectDisposedException.ThrowIf(IsDisposed, this);
#else
        if (IsDisposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
#endif
    }

    [LoggerMessage(LogLevel.Warning, "Leader election is already running")]
    partial void LogLeaderElectionIsAlreadyRunning();

    [LoggerMessage(LogLevel.Information, "Starting leader election for instance {instanceId}")]
    partial void LogStartingLeaderElectionForInstanceInstanceid(string instanceId);

    [LoggerMessage(LogLevel.Information, "Leadership acquired for instance {instanceId}")]
    partial void LogLeadershipAcquiredForInstanceInstanceid(string instanceId);

    [LoggerMessage(LogLevel.Debug, "Failed to acquire leadership, will retry")]
    partial void LogFailedToAcquireLeadershipWillRetry();

    [LoggerMessage(LogLevel.Warning, "Lost leadership during renewal for instance {instanceId}")]
    partial void LogLostLeadershipDuringRenewalForInstanceInstanceid(string instanceId);

    [LoggerMessage(LogLevel.Debug, "Leadership renewed successfully")]
    partial void LogLeadershipRenewedSuccessfully();

    [LoggerMessage(LogLevel.Error, "Unexpected error in leader loop")]
    partial void LogUnexpectedErrorInLeaderLoop(Exception errorMessage);

    [LoggerMessage(LogLevel.Information, "Stopping leader election for instance {instanceId}")]
    partial void LogStoppingLeaderElectionForInstanceInstanceid(string instanceId);

    [LoggerMessage(LogLevel.Debug, "Leader loop cancellation was expected")]
    partial void LogLeaderLoopCancellationWasExpected();

    [LoggerMessage(LogLevel.Debug, "Not the leader. Skipping task execution")]
    partial void LogNotTheLeaderSkippingTaskExecution();

    [LoggerMessage(LogLevel.Debug, "Executing task as leader")]
    partial void LogExecutingTaskAsLeader();

    [LoggerMessage(LogLevel.Error, "Error executing leader task")]
    partial void LogErrorExecutingLeaderTask(Exception errorMessage);

    [LoggerMessage(LogLevel.Error, "Error during async disposal")]
    partial void LogErrorDuringAsyncDisposal(Exception errorMessage);
}
