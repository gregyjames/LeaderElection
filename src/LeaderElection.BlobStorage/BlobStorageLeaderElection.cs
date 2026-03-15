using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Exception = System.Exception;

namespace LeaderElection.BlobStorage;
public sealed class BlobStorageLeaderElection : LeaderElectionBase<BlobStorageSettings>
{
    private readonly BlobContainerClient _containerClient;
    private readonly BlobClient _blobClient;
    private string? _currentLeaseId;

    public BlobStorageLeaderElection(
        BlobServiceClient blobServiceClient,
        IOptions<BlobStorageSettings> options,
        ILogger<BlobStorageLeaderElection> logger): base(options.Value, logger)
    {
        _containerClient = blobServiceClient.GetBlobContainerClient(Settings.ContainerName);
        _blobClient = _containerClient.GetBlobClient(Settings.BlobName);
    }
    
    public BlobStorageLeaderElection(BlobContainerClient client, BlobStorageSettings options, ILogger<BlobStorageLeaderElection> logger) : base(options, logger)
    {
        _containerClient = client ?? throw new ArgumentNullException(nameof(client));
        _blobClient = _containerClient.GetBlobClient(Settings.BlobName);
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDisposed)
            await EnsureBlobExistsAsync(cancellationToken);
        
        await base.StartAsync(cancellationToken);
    }
    
    private async Task EnsureBlobExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var containerExists = await _containerClient.ExistsAsync(cancellationToken);
            if (!containerExists.Value && Settings.CreateContainerIfNotExists)
            {
                Logger.LogDebug("Creating leader election container");
                await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            }

            var exists = await _blobClient.ExistsAsync(cancellationToken);
            if (!exists.Value)
            {
                Logger.LogDebug("Creating leader election blob");
                await _blobClient.UploadAsync(new BinaryData(Settings.InstanceId), overwrite: true, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error ensuring blob exists");
            throw;
        }
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureBlobExistsAsync(cancellationToken);
            var leaseClient = _blobClient.GetBlobLeaseClient();
            
            var leaseResponse = await leaseClient.AcquireAsync(Settings.LeaseDuration, cancellationToken: cancellationToken);
            
            if (leaseResponse?.Value?.LeaseId != null)
            {
                _currentLeaseId = leaseResponse.Value.LeaseId;
                Logger.LogDebug("Acquired lease with ID: {LeaseId}", _currentLeaseId);
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already exists
        {
            Logger.LogDebug("Lease already exists, cannot acquire leadership");
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error acquiring leadership");
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            return false;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            var leaseResponse = await leaseClient.RenewAsync(cancellationToken: cancellationToken);
            
            if (leaseResponse?.Value?.LeaseId != null)
            {
                Logger.LogDebug("Renewed lease successfully");
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            Logger.LogWarning("Blob not found during lease renewal");
            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease lost
        {
            Logger.LogWarning("Lease conflict during renewal - leadership lost");
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error renewing leadership");
            return false;
        }
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            return;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            await leaseClient.ReleaseAsync();
            
            _currentLeaseId = null;
            Logger.LogInformation("Leadership released for instance {InstanceId}", Settings.InstanceId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            Logger.LogDebug("Blob not found during lease release");
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already expired
        {
            Logger.LogDebug("Lease already expired during release");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error releasing leadership");
        }
    }
    private void ValidateOptions(bool selfProvidedInstance)
    {
        base.ValidateOptions();
        if (string.IsNullOrWhiteSpace(Settings.ContainerName))
            throw new ArgumentException("ContainerName cannot be null or empty", nameof(Settings.ContainerName));
        if (string.IsNullOrWhiteSpace(Settings.BlobName))
            throw new ArgumentException("BlobName cannot be null or empty", nameof(Settings.BlobName));
        
        if (Settings.LeaseDuration.TotalSeconds is < 15 or > 60)
        {
            throw new ArgumentException("LeaseDuration must be between 15 and 60 seconds for Azure Blob Storage", nameof(Settings.LeaseDuration));
        }
    }
}