using Microsoft.Extensions.Logging;

namespace LeaderElection;

public abstract class LeaderElectionBase<TSettings> : ILeaderElection
    where TSettings : LeaderElectionSettingsBase
{
    protected readonly TSettings settings;
    protected readonly ILogger logger;

    protected LeaderElectionBase(TSettings settings, ILogger logger)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private readonly SemaphoreSlim _leadershipSemaphore = new(1,1);
    private CancellationTokenSource? _leadershipLoopCancellationTokenSource = new();
    
    private volatile bool _isLeader;
    private int _disposedValue;
    private Task? _leadershipLoopTask;
    private DateTime _lastLeadershipRenewalTime = DateTime.MinValue;
    
    public event EventHandler<bool>? LeadershipChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    private bool IsDisposed => Volatile.Read(ref _disposedValue) == 1;
    public bool IsLeader => _isLeader && !IsDisposed;
    public DateTime LastLeadershipRenewal => _lastLeadershipRenewalTime;
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(GetType().Name);

        if (_leadershipLoopTask is { IsCompleted: false })
        {
            logger.LogWarning("Leader election is already running");
            return;
        }

        logger.LogInformation("Starting leader election for instance {InstanceId}", settings.InstanceId);

        _leadershipLoopCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _leadershipLoopTask = RunLeaderLoopAsync(_leadershipLoopCancellationTokenSource.Token);

        await Task.CompletedTask;
    }
    
    private async Task RunLeaderLoopAsync(CancellationToken token)
    {
        var retryCount = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_isLeader)
                {
                    if (await TryAcquireLeadershipAsync(token))
                    {
                        logger.LogInformation("Leadership acquired for instance {InstanceId}", settings.InstanceId);
                        retryCount = 0;
                    }
                    else
                    {
                        logger.LogDebug("Failed to acquire leadership, will retry");
                        retryCount++;
                    }
                }
                else
                {
                    if (!await RenewLeadershipInternalAsync(token))
                    {
                        logger.LogWarning("Lost leadership during renewal for instance {InstanceId}", settings.InstanceId);
                        SetLeadership(false);
                        retryCount++;
                    }
                    else
                    {
                        logger.LogDebug("Leadership renewed successfully");
                        _lastLeadershipRenewalTime = DateTime.UtcNow;
                        retryCount = 0;
                    }
                }

                var delay = retryCount > 0
                    ? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(retryCount, settings.MaxRetryAttempts)), 60))
                    : settings.RenewInterval;

                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in leader loop");
                ErrorOccurred?.Invoke(this, ex);
                retryCount++;

                try
                {
                    await Task.Delay(settings.RetryInterval, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public virtual Task StopAsync(CancellationToken cancellationToken = default) => IsDisposed ? Task.CompletedTask : InternalStopAsync(cancellationToken);
    private async Task InternalStopAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Stopping leader election for instance {InstanceId}", settings.InstanceId);

        if (_leadershipLoopCancellationTokenSource != null)
        {
            await _leadershipLoopCancellationTokenSource.CancelAsync();
        }

        if (_leadershipLoopTask != null)
        {
            try
            {
                await _leadershipLoopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Leader loop cancellation was expected");
            }
            finally
            {
                _leadershipLoopTask = null;
                _leadershipLoopCancellationTokenSource?.Dispose();
                _leadershipLoopCancellationTokenSource = null;
            }
        }

        if (settings.EnableGracefulShutdown && _isLeader)
        {
            await ReleaseLeadershipAsync();
        }

        if (_isLeader)
        {
            SetLeadership(false);
        }
    }
    
    protected abstract Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken);
    protected abstract Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken);
    protected abstract Task ReleaseLeadershipAsync();

    protected void SetLeadership(bool isLeader)
    {
        if (_isLeader != isLeader)
        {
            _isLeader = isLeader;
            _lastLeadershipRenewalTime = isLeader ? DateTime.UtcNow : DateTime.MinValue;
            LeadershipChanged?.Invoke(this, isLeader);
        }
    }
    
    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
            return false;

        await _leadershipSemaphore.WaitAsync(cancellationToken);
        try
        {
            var acquired = await TryAcquireLeadershipInternalAsync(cancellationToken);
            SetLeadership(acquired);
            return acquired;
        }
        finally
        {
            _leadershipSemaphore.Release();
        }
    }

    public async Task RunTaskIfLeaderAsync(Func<Task> task, CancellationToken cancellationToken = default)
    {
        if (!IsLeader)
        {
            logger.LogDebug("Not the leader. Skipping task execution");
            return;
        }

        try
        {
            logger.LogDebug("Executing task as leader");
            await task();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing leader task");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    public async Task RunTaskIfLeaderAsync(Action task, CancellationToken cancellationToken = default)
    {
        await RunTaskIfLeaderAsync(() => Task.Run(task, cancellationToken), cancellationToken);
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
            logger.LogError(ex, "Error during async disposal");
        }

        _leadershipLoopCancellationTokenSource?.Dispose();
        _leadershipSemaphore.Dispose();
    }
}