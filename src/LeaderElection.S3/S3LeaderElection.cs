using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace LeaderElection.S3;

public sealed class S3LeaderElection : ILeaderElection
{
    private readonly IMinioClient _client;
    private readonly S3Settings _options;
    private readonly ILogger<S3LeaderElection> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _leadershipSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private volatile bool _isLeader;
    private int _disposedValue; // 0 = not disposed, 1 = disposed
    private Task? _leaderLoopTask;
    private DateTime _lastLeadershipRenewal = DateTime.MinValue;
    private string? _lastEtag;

    public event EventHandler<bool>? LeadershipChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public S3LeaderElection(
        IMinioClient client,
        IOptions<S3Settings> options,
        ILogger<S3LeaderElection> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
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
            throw new ObjectDisposedException(nameof(S3LeaderElection));

        if (_leaderLoopTask is { IsCompleted: false })
        {
            _logger.LogWarning("Leader election is already running");
            return;
        }

        _logger.LogInformation("Starting S3 leader election for instance {InstanceId}", _options.InstanceId);

        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
        _leaderLoopTask = RunLeaderLoopAsync(combinedCts);

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        IsDisposed ? Task.CompletedTask : InternalStopAsync(cancellationToken);

    private async Task InternalStopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping S3 leader election for instance {InstanceId}", _options.InstanceId);

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
            _isLeader = false;
            LeadershipChanged?.Invoke(this, false);
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
                    _isLeader = true;
                    LeadershipChanged?.Invoke(this, true);
                }
            }
            else
            {
                if (_isLeader)
                {
                    _isLeader = false;
                    LeadershipChanged?.Invoke(this, false);
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
                            SetLeadership(true);
                            retryCount = 0;
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
                            retryCount = 0;
                        }
                    }

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
            // First check if the lease is current by trying to read it
            LeaseRecord? currentLease = null;
            string? currentEtag = null;
            try
            {
                var stat = await _client.StatObjectAsync(new StatObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(_options.ObjectKey), cancellationToken);
                currentEtag = stat.ETag;

                var memoryStream = new MemoryStream();
                await _client.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(_options.ObjectKey)
                    .WithCallbackStream((stream) => stream.CopyTo(memoryStream)), cancellationToken);

                memoryStream.Position = 0;
                currentLease = await JsonSerializer.DeserializeAsync<LeaseRecord>(memoryStream, _jsonOptions, cancellationToken);
                await memoryStream.DisposeAsync();
            }
            catch (ObjectNotFoundException)
            {
                _logger.LogDebug("Object not found, Trying to create it.");
            }

            var now = DateTime.UtcNow;
            if (currentLease != null && currentLease.LeaseUntilUtc > now && currentLease.HolderId != _options.InstanceId)
            {
                // Lease is still valid and held by someone else
                return false;
            }
            
            var leaseRecord = new LeaseRecord
            {
                HolderId = _options.InstanceId,
                LeaseUntilUtc = now.Add(_options.LeaseDuration)
            };

            // We use the etag here to prevent race conditions of multiple instances going for leadership at once
            var headers = new Dictionary<string, string>();
            if (currentEtag != null)
            {
                headers["If-Match"] = currentEtag;
            }
            else
            {
                headers["If-None-Match"] = "*";
            }

            _lastEtag = await PutLeaseAsync(leaseRecord, headers, cancellationToken);
            _lastLeadershipRenewal = DateTime.UtcNow;
            return true;
        }
        catch (ErrorResponseException ex) when (ex.Response.Code is "PreconditionFailed" or "AccessDenied")
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring S3 leadership");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    private async Task<bool> RenewLeadershipAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(_lastEtag))
            {
                return await TryAcquireLeadershipInternalAsync(cancellationToken);
            }

            var now = DateTime.UtcNow;
            var leaseRecord = new LeaseRecord
            {
                HolderId = _options.InstanceId,
                LeaseUntilUtc = now.Add(_options.LeaseDuration)
            };

            // Read current record to verify ETag
            try
            {
                var stat = await _client.StatObjectAsync(new StatObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(_options.ObjectKey), cancellationToken);
                
                if (stat.ETag != _lastEtag)
                {
                    _logger.LogWarning("S3 ETag mismatch during renewal. Expected {Expected}, got {Actual}", _lastEtag, stat.ETag);
                    return false;
                }

                var memoryStream = new MemoryStream();
                await _client.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(_options.ObjectKey)
                    .WithCallbackStream((stream) => stream.CopyTo(memoryStream)), cancellationToken);

                memoryStream.Position = 0;
                var currentLease =
                    await JsonSerializer.DeserializeAsync<LeaseRecord>(memoryStream, _jsonOptions,
                        cancellationToken);
                if (currentLease?.HolderId != _options.InstanceId)
                {
                    return false;
                }
                
                await memoryStream.DisposeAsync();
            }
            catch (Exception)
            {
                return false;
            }

            _lastEtag = await PutLeaseAsync(leaseRecord, new Dictionary<string, string> { ["If-Match"] = _lastEtag }, cancellationToken);
            _lastLeadershipRenewal = DateTime.UtcNow;
            return true;
        }
        catch (ErrorResponseException ex) when (ex.Response.Code is "PreconditionFailed" or "AccessDenied")
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renewing S3 leadership");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    private async Task ReleaseLeadershipAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_lastEtag)) return;
            
            var now = DateTime.UtcNow;
            var leaseRecord = new LeaseRecord
            {
                HolderId = _options.InstanceId,
                LeaseUntilUtc = now.AddSeconds(-1)
            };

            await PutLeaseAsync(leaseRecord, new Dictionary<string, string> { ["If-Match"] = _lastEtag }, default);
            _logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing S3 leadership");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private async Task<string> PutLeaseAsync(LeaseRecord record, IDictionary<string, string> headers, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(record, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream(bytes, writable: false);

        var request = new PutObjectArgs()
            .WithBucket(_options.BucketName)
            .WithObject(_options.ObjectKey)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType("application/json")
            .WithHeaders(new Dictionary<string, string>(headers));

        var response = await _client.PutObjectAsync(request, token);
        var etag = response?.Etag;

        if (string.IsNullOrEmpty(etag))
        {
            throw new InvalidOperationException("PutObjectAsync succeeded but no ETag was returned.");
        }

        return etag;
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
        if (string.IsNullOrWhiteSpace(_options.BucketName))
            throw new ArgumentException("BucketName cannot be null or empty", nameof(_options.BucketName));

        if (string.IsNullOrWhiteSpace(_options.ObjectKey))
            throw new ArgumentException("ObjectKey cannot be null or empty", nameof(_options.ObjectKey));

        if (string.IsNullOrWhiteSpace(_options.InstanceId))
            throw new ArgumentException("InstanceId cannot be null or empty", nameof(_options.InstanceId));

        if (_options.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("LeaseDuration must be positive", nameof(_options.LeaseDuration));

        if (_options.RenewInterval <= TimeSpan.Zero)
            throw new ArgumentException("RenewInterval must be positive", nameof(_options.RenewInterval));

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during async disposal");
        }

        _cancellationTokenSource.Dispose();
        _leadershipSemaphore.Dispose();
    }
}