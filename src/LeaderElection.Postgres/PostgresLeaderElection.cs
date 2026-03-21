namespace LeaderElection.Postgres;

using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class PostgresLeaderElection : ILeaderElection
{
    private readonly PostgresSettings _options;
    private readonly ILogger<PostgresLeaderElection> _logger;
    private readonly SemaphoreSlim _leadershipSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private volatile bool _isLeader;
    private int _disposedValue;
    private Task? _leaderLoopTask;
    private DateTime _lastLeadershipRenewal = DateTime.MinValue;
    private NpgsqlConnection? _activeConnection;

    public event EventHandler<bool>? LeadershipChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public PostgresLeaderElection(
        IOptions<PostgresSettings> options,
        ILogger<PostgresLeaderElection> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ValidateOptions();
    }

    public DateTime LastLeadershipRenewal => _lastLeadershipRenewal;
    public bool IsLeader => _isLeader && !IsDisposed;
    private bool IsDisposed => Volatile.Read(ref _disposedValue) == 1;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(PostgresLeaderElection));

        if (_leaderLoopTask is { IsCompleted: false })
            return;

        _logger.LogInformation("Starting PostgreSQL leader election for instance {InstanceId}", _options.InstanceId);

        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
        _leaderLoopTask = RunLeaderLoopAsync(combinedCts);

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        IsDisposed ? Task.CompletedTask : InternalStopAsync(cancellationToken);

    private async Task InternalStopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping PostgreSQL leader election for instance {InstanceId}", _options.InstanceId);

        await _cancellationTokenSource.CancelAsync();

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

        if (_options.EnableGracefulShutdown && _isLeader)
        {
            await ReleaseLeadershipAsync();
        }

        if (_isLeader)
        {
            SetLeadership(false);
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
            if (acquired)
            {
                if (!_isLeader)
                {
                    SetLeadership(true);
                }
            }
            else
            {
                if (_isLeader)
                {
                    SetLeadership(false);
                }
            }
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
            return;
        }

        try
        {
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
                            retryCount = 0;
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
                            _logger.LogWarning("Lost leadership during renewal for instance {InstanceId}", _options.InstanceId);
                            SetLeadership(false);
                            retryCount++;
                        }
                        else
                        {
                            retryCount = 0;
                        }
                    }

                    var delay = retryCount > 0
                        ? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60))
                        : _options.RetryInterval;

                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
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
            if (_activeConnection != null)
            {
                await _activeConnection.DisposeAsync();
                _activeConnection = null;
            }

            var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@LockId);", connection);
            cmd.Parameters.AddWithValue("LockId", _options.LockId);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result is true)
            {
                _activeConnection = connection;
                _lastLeadershipRenewal = DateTime.UtcNow;
                return true;
            }

            await connection.DisposeAsync();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring PostgreSQL leadership");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    private async Task<bool> RenewLeadershipAsync(CancellationToken cancellationToken)
    {
        if (_activeConnection is not { State: ConnectionState.Open })
        {
            return false;
        }

        try
        {
            //the leadership should be maintained as long as the connection is open, this is just a heartbeat
            await using var cmd = new NpgsqlCommand("SELECT 1;", _activeConnection);
            await cmd.ExecuteScalarAsync(cancellationToken);
            
            _lastLeadershipRenewal = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renewing PostgreSQL leadership; connection lost");
            ErrorOccurred?.Invoke(this, ex);
            
            if (_activeConnection != null)
            {
                await _activeConnection.DisposeAsync();
                _activeConnection = null;
            }
            return false;
        }
    }

    private async Task ReleaseLeadershipAsync()
    {
        if (_activeConnection == null)
            return;

        try
        {
            if (_activeConnection.State == ConnectionState.Open)
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@LockId);", _activeConnection);
                cmd.Parameters.AddWithValue("LockId", _options.LockId);
                await cmd.ExecuteScalarAsync(default);
            }
            
            _logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing PostgreSQL leadership");
            ErrorOccurred?.Invoke(this, ex);
        }
        finally
        {
            await _activeConnection.DisposeAsync();
            _activeConnection = null;
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
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new ArgumentException("ConnectionString cannot be null or empty", nameof(_options.ConnectionString));

        if (string.IsNullOrWhiteSpace(_options.InstanceId))
            throw new ArgumentException("InstanceId cannot be null or empty", nameof(_options.InstanceId));

        if (_options.RetryInterval <= TimeSpan.Zero)
            throw new ArgumentException("RetryInterval must be positive", nameof(_options.RetryInterval));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedValue, 1) == 1)
            return;

        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeAsyncCore()
    {
        try
        {
            await InternalStopAsync().ConfigureAwait(false);
            
            if (_activeConnection != null)
            {
                await _activeConnection.DisposeAsync();
                _activeConnection = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during async disposal");
        }

        _cancellationTokenSource.Dispose();
        _leadershipSemaphore.Dispose();
    }
}