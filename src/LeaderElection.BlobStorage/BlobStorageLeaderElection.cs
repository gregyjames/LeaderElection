using System.Diagnostics.CodeAnalysis;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeaderElection.BlobStorage;
[SuppressMessage("Design", "CA1031:Do not catch general exception types")]
public partial class BlobStorageLeaderElection : LeaderElectionBase<BlobStorageSettings>
{
    private readonly BlobContainerClient? _containerClient;
    private readonly BlobClient? _blobClient;
    private readonly BlobStorageSettings _options;
    private string? _currentLeaseId;

    public BlobStorageLeaderElection(
        BlobServiceClient? blobServiceClient,
        IOptions<BlobStorageSettings>? options,
        ILogger<BlobStorageLeaderElection> logger) : base(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _containerClient = blobServiceClient?.GetBlobContainerClient(_options.ContainerName);

        _blobClient = _containerClient?.GetBlobClient(_options.BlobName);
    }

    public BlobStorageLeaderElection(BlobContainerClient client, BlobStorageSettings options, ILogger<BlobStorageLeaderElection> logger) : base(options ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _containerClient = client ?? throw new ArgumentNullException(nameof(client));
        _options = options;

        _blobClient = _containerClient.GetBlobClient(_options.BlobName);
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_options.CreateContainerIfNotExists)
            {
                await EnsureBlobExistsAsync(cancellationToken).ConfigureAwait(false);
            }

            var leaseClient = _blobClient.GetBlobLeaseClient();
            var leaseResponse = await leaseClient.AcquireAsync(_options.LeaseDuration, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (leaseResponse?.Value?.LeaseId != null)
            {
                _currentLeaseId = leaseResponse.Value.LeaseId;
                LogAcquiredLeaseWithIdLeaseId(Logger, _currentLeaseId);
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already exists
        {
            LogLeaseAlreadyExistsCannotAcquireLeadership(Logger);
            return false;
        }
        catch (Exception ex)
        {
            LogErrorAcquiringLeadership(Logger, ex);
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            LogNoCurrentLeaseIdCannotRenewLeadership(Logger);
            return false;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            var leaseResponse = await leaseClient.RenewAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            if (leaseResponse?.Value?.LeaseId != null)
            {
                LogRenewedLeaseSuccessfully(Logger);
                return true;
            }

            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            LogBlobNotFoundDuringLeaseRenewal(Logger);
            return false;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease lost
        {
            LogLeaseConflictDuringRenewalLeadershipLost(Logger);
            return false;
        }
        catch (Exception ex)
        {
            LogErrorRenewingLeadership(Logger, ex);
            return false;
        }
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            LogNoLeaseIdToRelease(Logger);
            return;
        }

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            await leaseClient.ReleaseAsync().ConfigureAwait(false);

            _currentLeaseId = null;
            LogLeadershipReleasedForInstanceInstanceId(Logger, _options.InstanceId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) // Not Found - blob doesn't exist
        {
            LogBlobNotFoundDuringLeaseRelease(Logger);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409) // Conflict - lease already expired
        {
            LogLeaseAlreadyExpiredDuringRelease(Logger);
        }
        catch (Exception ex)
        {
            LogErrorReleasingLeadership(Logger, ex);
        }
    }

    private async Task EnsureBlobExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_containerClient != null)
            {
                var containerExists = await _containerClient.ExistsAsync(cancellationToken).ConfigureAwait(false);
                if (!containerExists.Value && _options.CreateContainerIfNotExists)
                {
                    LogCreatingLeaderElectionContainer(Logger);
                    await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            if (_blobClient != null)
            {
                var exists = await _blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false);
                if (!exists.Value)
                {
                    LogCreatingLeaderElectionBlob(Logger);
                    await _blobClient.UploadAsync(new BinaryData(_options.InstanceId), overwrite: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            LogErrorEnsuringBlobExists(Logger, ex);
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
