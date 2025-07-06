using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaderElection.BlobStorage;
public class BlobStorageLeaderElection : ILeaderElection
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly BlobContainerClient _containerClient;
    private readonly BlobClient _blobClient;
    private readonly BlobStorageSettings _options;
    private readonly ILogger<BlobStorageLeaderElection> _logger;
    private readonly SemaphoreSlim _leadershipSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    private volatile bool _isLeader;
    private volatile bool _isDisposed;
    private Task? _leaderLoopTask;
    private DateTime _lastLeadershipRenewal = DateTime.MinValue;
    private string? _currentLeaseId;

    public event EventHandler<bool>? LeadershipChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public BlobStorageLeaderElection(
        BlobServiceClient blobServiceClient,
        IOptions<BlobStorageSettings> options,
        ILogger<BlobStorageLeaderElection> logger)
    {
        _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        ValidateOptions();
        
        _containerClient = _blobServiceClient.GetBlobContainerClient(_options.ContainerName);
        _blobClient = _containerClient.GetBlobClient(_options.BlobName);
    }

    public bool IsLeader => _isLeader && !_isDisposed;
    public DateTime LastLeadershipRenewal => _lastLeadershipRenewal;
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(BlobStorageLeaderElection));

        if (_leaderLoopTask != null && !_leaderLoopTask.IsCompleted)
        {
            _logger.LogWarning("Leader election is already running");
            return;
        }

        _logger.LogInformation("Starting Blob Storage leader election for instance {InstanceId}", _options.InstanceId);
        
        // Ensure container exists
        if (_options.CreateContainerIfNotExists)
        {
            await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }
        
        // Ensure blob exists
        await EnsureBlobExistsAsync(cancellationToken);
        
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
        _leaderLoopTask = RunLeaderLoopAsync(combinedCts.Token);
        
        await Task.CompletedTask; // Return immediately, let the loop run in background
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            return;

        _logger.LogInformation("Stopping Blob Storage leader election for instance {InstanceId}", _options.InstanceId);
        
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

    private async Task EnsureBlobExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exists = await _blobClient.ExistsAsync(cancellationToken);
            if (!exists.Value)
            {
                _logger.LogDebug("Creating leader election blob");
                await _blobClient.UploadAsync(new BinaryData(_options.InstanceId), overwrite: true, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring blob exists");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient();
            var leaseResponse = await leaseClient.AcquireAsync(_options.LeaseDuration, cancellationToken: cancellationToken);
            
            if (leaseResponse?.Value?.LeaseId != null)
            {
                _currentLeaseId = leaseResponse.Value.LeaseId;
                _lastLeadershipRenewal = DateTime.UtcNow;
                _logger.LogDebug("Acquired lease with ID: {LeaseId}", _currentLeaseId);
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already exists
        {
            _logger.LogDebug("Lease already exists, cannot acquire leadership");
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
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            _logger.LogWarning("No current lease ID, cannot renew leadership");
            return false;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            var leaseResponse = await leaseClient.RenewAsync(cancellationToken: cancellationToken);
            
            if (leaseResponse?.Value?.LeaseId != null)
            {
                _lastLeadershipRenewal = DateTime.UtcNow;
                _logger.LogDebug("Renewed lease successfully");
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            _logger.LogWarning("Blob not found during lease renewal");
            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease lost
        {
            _logger.LogWarning("Lease conflict during renewal - leadership lost");
            return false;
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
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            _logger.LogDebug("No lease ID to release");
            return;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            await leaseClient.ReleaseAsync();
            
            _currentLeaseId = null;
            _logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            _logger.LogDebug("Blob not found during lease release");
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already expired
        {
            _logger.LogDebug("Lease already expired during release");
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
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new ArgumentException("ConnectionString cannot be null or empty", nameof(_options.ConnectionString));
        
        if (string.IsNullOrWhiteSpace(_options.ContainerName))
            throw new ArgumentException("ContainerName cannot be null or empty", nameof(_options.ContainerName));
        
        if (string.IsNullOrWhiteSpace(_options.BlobName))
            throw new ArgumentException("BlobName cannot be null or empty", nameof(_options.BlobName));
        
        if (string.IsNullOrWhiteSpace(_options.InstanceId))
            throw new ArgumentException("InstanceId cannot be null or empty", nameof(_options.InstanceId));
        
        if (_options.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("LeaseDuration must be positive", nameof(_options.LeaseDuration));
        
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