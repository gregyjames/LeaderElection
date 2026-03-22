using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaderElection.BlobStorage;
public class BlobStorageLeaderElection : LeaderElectionBase<BlobStorageSettings>
{
    private readonly BlobContainerClient _containerClient;
    private readonly BlobClient _blobClient;
    private readonly BlobStorageSettings _options;
    private string? _currentLeaseId;

    public BlobStorageLeaderElection(
        BlobServiceClient blobServiceClient,
        IOptions<BlobStorageSettings> options,
        ILogger<BlobStorageLeaderElection> logger) : base(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        
        ValidateOptions(false);
        
        _containerClient = blobServiceClient.GetBlobContainerClient(_options.ContainerName);
        _blobClient = _containerClient.GetBlobClient(_options.BlobName);
    }
    
    public BlobStorageLeaderElection(BlobContainerClient client, BlobStorageSettings options, ILogger<BlobStorageLeaderElection> logger) : base(options ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _containerClient = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        
        ValidateOptions(true);
        
        _blobClient = _containerClient.GetBlobClient(_options.BlobName);
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_options.CreateContainerIfNotExists)
            {
                await EnsureBlobExistsAsync(cancellationToken);
            }

            var leaseClient = _blobClient.GetBlobLeaseClient();
            var leaseResponse = await leaseClient.AcquireAsync(_options.LeaseDuration, cancellationToken: cancellationToken);
            
            if (leaseResponse?.Value?.LeaseId != null)
            {
                _currentLeaseId = leaseResponse.Value.LeaseId;
                logger.LogDebug("Acquired lease with ID: {LeaseId}", _currentLeaseId);
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already exists
        {
            logger.LogDebug("Lease already exists, cannot acquire leadership");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring leadership");
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            logger.LogWarning("No current lease ID, cannot renew leadership");
            return false;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            var leaseResponse = await leaseClient.RenewAsync(cancellationToken: cancellationToken);
            
            if (leaseResponse?.Value?.LeaseId != null)
            {
                logger.LogDebug("Renewed lease successfully");
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            logger.LogWarning("Blob not found during lease renewal");
            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease lost
        {
            logger.LogWarning("Lease conflict during renewal - leadership lost");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error renewing leadership");
            return false;
        }
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            logger.LogDebug("No lease ID to release");
            return;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            await leaseClient.ReleaseAsync();
            
            _currentLeaseId = null;
            logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            logger.LogDebug("Blob not found during lease release");
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already expired
        {
            logger.LogDebug("Lease already expired during release");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing leadership");
        }
    }

    private async Task EnsureBlobExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var containerExists = await _containerClient.ExistsAsync(cancellationToken);
            if (!containerExists.Value && _options.CreateContainerIfNotExists)
            {
                logger.LogDebug("Creating leader election container");
                await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            }

            var exists = await _blobClient.ExistsAsync(cancellationToken);
            if (!exists.Value)
            {
                logger.LogDebug("Creating leader election blob");
                await _blobClient.UploadAsync(new BinaryData(_options.InstanceId), overwrite: true, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error ensuring blob exists");
        }
    }

    private void ValidateOptions(bool selfProvidedInstance)
    {
        if (!selfProvidedInstance)
        {
            if (string.IsNullOrWhiteSpace(_options.ConnectionString))
                throw new ArgumentException("ConnectionString cannot be null or empty",
                    nameof(_options.ConnectionString));

            if (string.IsNullOrWhiteSpace(_options.ContainerName))
                throw new ArgumentException("ContainerName cannot be null or empty", nameof(_options.ContainerName));
        }
    }
}