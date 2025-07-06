using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace LeaderElection.Redis;

public class RedisLeaderElection : ILeaderElection
{
    private readonly IDatabase _redis;
    private readonly RedisSettings _options;
    private readonly ILogger<RedisLeaderElection> _logger;
    private readonly SemaphoreSlim _leadershipSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    private volatile bool _isLeader;
    private volatile bool _isDisposed;
    private Task? _leaderLoopTask;
    private DateTime _lastLeadershipRenewal = DateTime.MinValue;

    public event EventHandler<bool>? LeadershipChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public RedisLeaderElection(
        IConnectionMultiplexer connectionMultiplexer, 
        IOptions<RedisSettings> options,
        ILogger<RedisLeaderElection> logger)
    {
        _redis = connectionMultiplexer.GetDatabase() ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        ValidateOptions();
    }

    public DateTime LastLeadershipRenewal => _lastLeadershipRenewal;
    public bool IsLeader => _isLeader && !_isDisposed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(RedisLeaderElection));

        if (_leaderLoopTask != null && !_leaderLoopTask.IsCompleted)
        {
            _logger.LogWarning("Leader election is already running");
            return;
        }

        _logger.LogInformation("Starting Redis leader election for instance {InstanceId}", _options.InstanceId);
        
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
        _leaderLoopTask = RunLeaderLoopAsync(combinedCts.Token);
        
        await Task.CompletedTask; // Return immediately, let the loop run in background
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            return;

        _logger.LogInformation("Stopping Redis leader election for instance {InstanceId}", _options.InstanceId);
        
        _cancellationTokenSource.Cancel();
        
        if (_leaderLoopTask != null)
        {
            try
            {
                await _leaderLoopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Leader loop cancellation was expected");
            }
        }
    }

    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            return false;

        await _leadershipSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await TryAcquireLeadershipInternalAsync(cancellationToken);
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
            _logger.LogDebug("Not the leader. Skipping task execution");
            return;
        }

        try
        {
            _logger.LogDebug("Executing task as leader");
            await task();
        }
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

    private async Task RunLeaderLoopAsync(CancellationToken cancellationToken)
    {
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
                        SetLeadership(true);
                        retryCount = 0; // Reset retry count on success
                    }
                    else
                    {
                        _logger.LogDebug("Failed to acquire leadership, will retry");
                        retryCount++;
                    }
                }
                else
                {
                    if (!await RenewLeadershipAsync(cancellationToken))
                    {
                        _logger.LogWarning("Lost leadership during renewal for instance {InstanceId}", _options.InstanceId);
                        SetLeadership(false);
                        retryCount++;
                    }
                    else
                    {
                        _logger.LogDebug("Leadership renewed successfully");
                        retryCount = 0; // Reset retry count on success
                    }
                }

                // Exponential backoff for retries
                var delay = retryCount > 0 
                    ? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60)) 
                    : _options.RenewInterval;
                
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Leader loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in leader loop");
                ErrorOccurred?.Invoke(this, ex);
                retryCount++;
                
                try
                {
                    await Task.Delay(_options.RetryInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Cleanup on shutdown
        if (_options.EnableGracefulShutdown && _isLeader)
        {
            await ReleaseLeadershipAsync();
        }
    }

    private async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _redis.StringSetAsync(
                key: _options.LockKey,
                value: _options.InstanceId,
                expiry: _options.LockExpiry,
                when: When.NotExists
            );

            if (result)
            {
                _lastLeadershipRenewal = DateTime.UtcNow;
            }

            return result;
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
            var script = @"
                if redis.call('GET', KEYS[1]) == ARGV[1]
                then
                    return redis.call('PEXPIRE', KEYS[1], ARGV[2])
                else
                    return 0
                end";

            var result = await _redis.ScriptEvaluateAsync(
                script, 
                new RedisKey[] { _options.LockKey }, 
                new RedisValue[] { _options.InstanceId, (int)_options.LockExpiry.TotalMilliseconds }
            );

            var success = (int)result != 0;
            if (success)
            {
                _lastLeadershipRenewal = DateTime.UtcNow;
            }

            return success;
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
            var script = @"
                if redis.call('GET', KEYS[1]) == ARGV[1]
                then
                    return redis.call('DEL', KEYS[1])
                else
                    return 0
                end";

            await _redis.ScriptEvaluateAsync(
                script, 
                new RedisKey[] { _options.LockKey }, 
                new RedisValue[] { _options.InstanceId }
            );

            _logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing leadership");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private void SetLeadership(bool isLeader)
    {
        if (_isLeader != isLeader)
        {
            _isLeader = isLeader;
            LeadershipChanged?.Invoke(this, isLeader);
        }
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.LockKey))
            throw new ArgumentException("LockKey cannot be null or empty", nameof(_options.LockKey));
        
        if (string.IsNullOrWhiteSpace(_options.InstanceId))
            throw new ArgumentException("InstanceId cannot be null or empty", nameof(_options.InstanceId));
        
        if (_options.LockExpiry <= TimeSpan.Zero)
            throw new ArgumentException("LockExpiry must be positive", nameof(_options.LockExpiry));
        
        if (_options.RenewInterval <= TimeSpan.Zero)
            throw new ArgumentException("RenewInterval must be positive", nameof(_options.RenewInterval));
        
        if (_options.RetryInterval <= TimeSpan.Zero)
            throw new ArgumentException("RetryInterval must be positive", nameof(_options.RetryInterval));
        
        if (_options.MaxRetryAttempts < 0)
            throw new ArgumentException("MaxRetryAttempts cannot be negative", nameof(_options.MaxRetryAttempts));
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        
        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal");
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _leadershipSemaphore.Dispose();
        }
    }
}
