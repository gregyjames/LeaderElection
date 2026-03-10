using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaderElection.DistributedCache;

public class DistributedCacheLeaderElection : ILeaderElection, IDisposable
{
    private readonly IDistributedCache _cache;
    private readonly DistributedCacheSettings _options;
    private readonly ILogger<DistributedCacheLeaderElection> _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    private volatile bool _isLeader;
    private int _disposedValue; // 0 = not disposed, 1 = disposed
    private Task? _leaderLoopTask;
    private DateTime _lastLeadershipRenewal = DateTime.MinValue;

    public event EventHandler<bool>? LeadershipChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public DistributedCacheLeaderElection(
        IDistributedCache cache,
        IOptions<DistributedCacheSettings> options,
        ILogger<DistributedCacheLeaderElection> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public DateTime LastLeadershipRenewal => _lastLeadershipRenewal;
    public bool IsLeader => _isLeader && !IsDisposed;
    private bool IsDisposed => Volatile.Read(ref _disposedValue) == 1;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(DistributedCacheLeaderElection));
        if (_leaderLoopTask != null && !_leaderLoopTask.IsCompleted) return Task.CompletedTask;

        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
        _leaderLoopTask = RunLeaderLoopAsync(combinedCts);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed) return;

        _cancellationTokenSource.Cancel();
        
        if (_leaderLoopTask != null)
        {
            try { await _leaderLoopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
        
        await ReleaseLeadershipAsync();
        if (_isLeader)
        {
            _isLeader = false;
            LeadershipChanged?.Invoke(this, false);
        }
    }

    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed) return false;

        var acquired = await TryAcquireLeadershipInternalAsync(cancellationToken);
        if (acquired && !_isLeader)
        {
            _isLeader = true;
            LeadershipChanged?.Invoke(this, true);
        }
        else if (!acquired && _isLeader)
        {
            _isLeader = false;
            LeadershipChanged?.Invoke(this, false);
        }
        return acquired;
    }

    public async Task RunTaskIfLeaderAsync(Func<Task> task, CancellationToken cancellationToken = default)
    {
        if (!IsLeader) return;
        
        try { await task(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing leader task");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    public async Task RunTaskIfLeaderAsync(Action task, CancellationToken cancellationToken = default)
    {
        await RunTaskIfLeaderAsync(() => Task.Run(task, cancellationToken), cancellationToken);
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
                            _logger.LogInformation("Leadership acquired for instance {InstanceId}", _options.InstanceId);
                            retryCount = 0; // Reset retry count on success
                        }
                        else
                        {
                            retryCount++;
                        }
                    }
                    else
                    {
                        if (!await RenewLeadershipAsync(cancellationToken))
                        {
                            _logger.LogWarning("Lost leadership for instance {InstanceId}", _options.InstanceId);
                            _isLeader = false;
                            LeadershipChanged?.Invoke(this, false);
                            retryCount++;
                        }
                        else
                        {
                            retryCount = 0; // Reset retry count on success
                        }
                    }

                    // Exponential backoff for retries
                    var delay = retryCount > 0 
                        ? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60)) 
                        : _options.RenewInterval;
                    
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in leader loop");
                    ErrorOccurred?.Invoke(this, ex);
                    retryCount++;
                    await Task.Delay(_options.RetryInterval, cancellationToken);
                }
            }

            if (_options.EnableGracefulShutdown && _isLeader)
            {
                await ReleaseLeadershipAsync();
            }
        }
        finally
        {
            combinedCts.Dispose();
        }
    }

    private async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken);
            if (!string.IsNullOrEmpty(currentValue)) return false;

            await _cache.SetStringAsync(_options.LockKey, _options.InstanceId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _options.LockExpiry
            }, cancellationToken);

            var verifyValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken);
            if (verifyValue == _options.InstanceId)
            {
                _lastLeadershipRenewal = DateTime.UtcNow;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring leadership");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    private async Task<bool> RenewLeadershipAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentValue = await _cache.GetStringAsync(_options.LockKey, cancellationToken);
            if (currentValue != _options.InstanceId) return false;

            await _cache.SetStringAsync(_options.LockKey, _options.InstanceId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _options.LockExpiry
            }, cancellationToken);

            _lastLeadershipRenewal = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renewing leadership");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    private async Task ReleaseLeadershipAsync()
    {
        try
        {
            var currentValue = await _cache.GetStringAsync(_options.LockKey);
            if (currentValue == _options.InstanceId)
            {
                await _cache.RemoveAsync(_options.LockKey);
                _logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing leadership");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedValue, 1) == 1)
            return;

        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during async disposal");
        }
        finally
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposedValue, 1) == 1)
            return;

        if (disposing)
        {
            try
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during synchronous disposal");
            }
        }
    }
}
