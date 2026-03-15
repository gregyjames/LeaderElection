using Microsoft.Extensions.Logging;

namespace LeaderElection;

public abstract class LeaderElectionBase<TSettings> : ILeaderElection
    where TSettings : LeaderElectionSettings
{
    protected readonly TSettings Settings;
    protected readonly ILogger Logger;
    private readonly SemaphoreSlim _leadershipSemaphore = new(1,1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    private volatile bool _isLeader;
    private int _disposedValue;
    private Task _leadershipLoopTask;
    private DateTime _lastLeadershipRenewalTime = DateTime.MinValue;
    
    public event EventHandler<bool>? LeadershipChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    protected LeaderElectionBase(TSettings settings, ILogger logger)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ValidateOptions();
    }
    
    protected virtual void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(Settings.InstanceId))
            throw new ArgumentException("InstanceId cannot be null or empty", nameof(Settings.InstanceId));
        if (Settings.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("LeaseDuration must be positive", nameof(Settings.LeaseDuration));
        if (Settings.RenewInterval <= TimeSpan.Zero)
            throw new ArgumentException("RenewInterval must be positive", nameof(Settings.RenewInterval));
        if (Settings.RetryInterval <= TimeSpan.Zero)
            throw new ArgumentException("RetryInterval must be positive", nameof(Settings.RetryInterval));
        if (Settings.MaxRetryAttempts < 0)
            throw new ArgumentException("MaxRetryAttempts must be positive", nameof(Settings.MaxRetryAttempts));
    }
    
    protected bool IsDisposed => Volatile.Read(ref _disposedValue) == 1;
    public bool IsLeader => _isLeader && !IsDisposed;
    public DateTime LastLeadershipRenewal => _lastLeadershipRenewalTime;
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(GetType().Name);

        if (_leadershipLoopTask is { IsCompleted: false })
        {
            Logger.LogWarning("Leader election is already running");
            return;
        }

        Logger.LogInformation("Starting leader election for instance {InstanceId}", Settings.InstanceId);

        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
        _leadershipLoopTask = RunLeaderLoopAsync(combinedCts);

        await Task.CompletedTask;
    }
    
    private async Task RunLeaderLoopAsync(CancellationTokenSource combinedCts)
    {
        try
        {
            var cancellationToken = combinedCts.Token;
            var retryCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_isLeader)
                    {
                        if (await TryAcquireLeadershipAsync(cancellationToken))
                        {
                            Logger.LogInformation("Leadership acquired for instance {InstanceId}", Settings.InstanceId);
                            retryCount = 0;
                        }
                        else
                        {
                            Logger.LogDebug("Failed to acquire leadership, will retry");
                            retryCount++;
                        }
                    }
                    else
                    {
                        if (!await RenewLeadershipInternalAsync(cancellationToken))
                        {
                            Logger.LogWarning("Lost leadership during renewal for instance {InstanceId}", Settings.InstanceId);
                            SetLeadership(false);
                            retryCount++;
                        }
                        else
                        {
                            Logger.LogDebug("Leadership renewed successfully");
                            _lastLeadershipRenewalTime = DateTime.UtcNow;
                            retryCount = 0;
                        }
                    }

                    var delay = retryCount > 0
                        ? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(retryCount, Settings.MaxRetryAttempts)), 60))
                        : Settings.RenewInterval;

                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Unexpected error in leader loop");
                    ErrorOccurred?.Invoke(this, ex);
                    retryCount++;

                    try
                    {
                        await Task.Delay(Settings.RetryInterval, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            combinedCts.Dispose();
        }
    }

    public virtual Task StopAsync(CancellationToken cancellationToken = default) => IsDisposed ? Task.CompletedTask : InternalStopAsync(cancellationToken);
    private async Task InternalStopAsync(CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Stopping leader election for instance {InstanceId}", Settings.InstanceId);

        await _cancellationTokenSource.CancelAsync();

        if (_leadershipLoopTask != null)
        {
            try
            {
                await _leadershipLoopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("Leader loop cancellation was expected");
            }
        }

        if (Settings.EnableGracefulShutdown && _isLeader)
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
            Logger.LogDebug("Not the leader. Skipping task execution");
            return;
        }

        try
        {
            Logger.LogDebug("Executing task as leader");
            await task();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error executing leader task");
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
            Logger.LogError(ex, "Error during async disposal");
        }

        _cancellationTokenSource.Dispose();
        _leadershipSemaphore.Dispose();
    }
}