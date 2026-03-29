using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaderElection.BlobStorage;
public partial class BlobStorageLeaderElection : LeaderElectionBase<BlobStorageSettings>
{
    private readonly BlobContainerClient? _containerClient;
    private readonly BlobClient? _blobClient;
    private string? _currentLeaseId;

    public BlobStorageLeaderElection(
        BlobServiceClient? blobServiceClient,
        IOptions<BlobStorageSettings>? options,
        ILogger<BlobStorageLeaderElection> logger) : base(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _containerClient = blobServiceClient?.GetBlobContainerClient(_settings.ContainerName);

        _blobClient = _containerClient?.GetBlobClient(_settings.BlobName);
    }

    public BlobStorageLeaderElection(BlobContainerClient client, BlobStorageSettings settings, ILogger<BlobStorageLeaderElection> logger) : base(settings ?? throw new ArgumentNullException(nameof(settings)), logger)
    {
        _containerClient = client ?? throw new ArgumentNullException(nameof(client));

        _blobClient = _containerClient.GetBlobClient(_settings.BlobName);
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_settings.CreateContainerIfNotExists)
            {
                await EnsureBlobExistsAsync(cancellationToken).ConfigureAwait(false);
            }

            var leaseClient = _blobClient.GetBlobLeaseClient();
            var leaseResponse = await leaseClient.AcquireAsync(_settings.LeaseDuration, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (leaseResponse?.Value?.LeaseId != null)
            {
                _currentLeaseId = leaseResponse.Value.LeaseId;
                LogAcquiredLeaseWithIdLeaseId(_logger, _currentLeaseId);
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already exists
        {
            LogLeaseAlreadyExistsCannotAcquireLeadership(_logger);
            return false;
        }
        catch (Exception ex)
        {
            LogErrorAcquiringLeadership(_logger, ex);
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            LogNoCurrentLeaseIdCannotRenewLeadership(_logger);
            return false;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            var leaseResponse = await leaseClient.RenewAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            if (leaseResponse?.Value?.LeaseId != null)
            {
                LogRenewedLeaseSuccessfully(_logger);
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            LogBlobNotFoundDuringLeaseRenewal(_logger);
            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease lost
        {
            LogLeaseConflictDuringRenewalLeadershipLost(_logger);
            return false;
        }
        catch (Exception ex)
        {
            LogErrorRenewingLeadership(_logger, ex);
            return false;
        }
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            LogNoLeaseIdToRelease(_logger);
            return;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            await leaseClient.ReleaseAsync().ConfigureAwait(false);

            _currentLeaseId = null;
            LogLeadershipReleasedForInstanceInstanceId(_logger, _settings.InstanceId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            LogBlobNotFoundDuringLeaseRelease(_logger);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already expired
        {
            LogLeaseAlreadyExpiredDuringRelease(_logger);
        }
        catch (Exception ex)
        {
            LogErrorReleasingLeadership(_logger, ex);
        }
    }

    private async Task EnsureBlobExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_containerClient != null)
            {
                var containerExists = await _containerClient.ExistsAsync(cancellationToken).ConfigureAwait(false);
                if (!containerExists.Value && _settings.CreateContainerIfNotExists)
                {
                    LogCreatingLeaderElectionContainer(_logger);
                    await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            if (_blobClient != null)
            {
                var exists = await _blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false);
                if (!exists.Value)
                {
                    LogCreatingLeaderElectionBlob(_logger);
                    await _blobClient.UploadAsync(new BinaryData(_settings.InstanceId), overwrite: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            LogErrorEnsuringBlobExists(_logger, ex);
        }
    }

    [LoggerMessage(LogLevel.Debug, "Acquired lease with ID: {leaseId}")]
    static partial void LogAcquiredLeaseWithIdLeaseId(ILogger logger, string leaseId);

    [LoggerMessage(LogLevel.Debug, "Lease already exists, cannot acquire leadership")]
    static partial void LogLeaseAlreadyExistsCannotAcquireLeadership(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Error acquiring leadership")]
    static partial void LogErrorAcquiringLeadership(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "No current lease ID, cannot renew leadership")]
    static partial void LogNoCurrentLeaseIdCannotRenewLeadership(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "Renewed lease successfully")]
    static partial void LogRenewedLeaseSuccessfully(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Blob not found during lease renewal")]
    static partial void LogBlobNotFoundDuringLeaseRenewal(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Lease conflict during renewal - leadership lost")]
    static partial void LogLeaseConflictDuringRenewalLeadershipLost(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Error renewing leadership")]
    static partial void LogErrorRenewingLeadership(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Debug, "No lease ID to release")]
    static partial void LogNoLeaseIdToRelease(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Leadership released for instance {instanceId}")]
    static partial void LogLeadershipReleasedForInstanceInstanceId(ILogger logger, string instanceId);

    [LoggerMessage(LogLevel.Debug, "Blob not found during lease release")]
    static partial void LogBlobNotFoundDuringLeaseRelease(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "Lease already expired during release")]
    static partial void LogLeaseAlreadyExpiredDuringRelease(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Error releasing leadership")]
    static partial void LogErrorReleasingLeadership(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Debug, "Creating leader election container")]
    static partial void LogCreatingLeaderElectionContainer(ILogger logger);

    [LoggerMessage(LogLevel.Debug, "Creating leader election blob")]
    static partial void LogCreatingLeaderElectionBlob(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Error ensuring blob exists")]
    static partial void LogErrorEnsuringBlobExists(ILogger logger, Exception exception);
}
