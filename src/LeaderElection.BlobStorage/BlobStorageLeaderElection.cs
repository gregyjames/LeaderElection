using System.Diagnostics;
using System.Net;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace LeaderElection.BlobStorage;

/// <summary>
/// Leader Election implementation that uses blob leases for leader election.
/// Each contender will attempt to acquire a lease on the same blob, and the one
/// that holds the lease is the leader. The lease will be automatically released
/// if the contender fails to renew it within the lease duration, allowing other
/// contenders to acquire leadership.
///
/// This implementation relies on Azure Blob Storage's strong consistency and lease
/// mechanism to ensure that only one contender can be the leader at any given time.
/// </summary>
public partial class BlobStorageLeaderElection : LeaderElectionBase<BlobStorageSettings>
{
    private BlobClient? _blobClient;
    private string? _currentLeaseId;

    public BlobStorageLeaderElection(
        BlobStorageSettings settings,
        ILogger<BlobStorageLeaderElection>? logger = null,
        TimeProvider? timeProvider = null
    )
        : base(settings ?? throw new ArgumentNullException(nameof(settings)), logger, timeProvider)
    { }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrEmpty(_currentLeaseId))
        {
            LogLeaseAlreadyAcquired(_blobClient!.Uri.GetLeftPart(UriPartial.Path));
            return false;
        }

        var blobClient = await CreateBlobClientAsync(cancellationToken).ConfigureAwait(false);

        await EnsureBlobExistsAsync(blobClient, cancellationToken).ConfigureAwait(false);

        var success = false;
        try
        {
            var leaseClient = blobClient.GetBlobLeaseClient();
            var leaseResponse = await leaseClient
                .AcquireAsync(_settings.LeaseDuration, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            success = true;
            _currentLeaseId = leaseResponse.Value.LeaseId;
            _blobClient = blobClient;
            LogAcquiredLeaseWithIdLeaseId(
                _blobClient.Uri.GetLeftPart(UriPartial.Path),
                _currentLeaseId
            );
        }
        catch (Azure.RequestFailedException ex)
        {
            var logLevel =
                ex.ErrorCode == BlobErrorCode.LeaseAlreadyPresent // another instance holds the lease - very common
                    ? LogLevel.Debug
                    : LogLevel.Error;

#pragma warning disable CA1873 // Avoid potentially expensive logging
            LogFailureAcquiringLease(
                logLevel,
                blobClient.Uri.GetLeftPart(UriPartial.Path),
                ex.Status,
                ex.ErrorCode
            );
#pragma warning restore CA1873 // Avoid potentially expensive logging
        }

        return success;
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            LogNoCurrentLeaseToRenew();
            return false;
        }

        Debug.Assert(_blobClient != null);

        var success = false;
        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            var leaseResponse = await leaseClient
                .RenewAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            success = true;
            Debug.Assert(_currentLeaseId == leaseResponse.Value.LeaseId);
            LogLeaseRenewed(_blobClient.Uri.GetLeftPart(UriPartial.Path), _currentLeaseId);
        }
        catch (Azure.RequestFailedException ex)
        {
            // this is unexpected since we should own the lease.
            // Log as a warning and give up our leadership.
            var logLevel = (HttpStatusCode)ex.Status switch
            {
                HttpStatusCode.NotFound => LogLevel.Warning, // blob deleted?
                HttpStatusCode.Conflict => LogLevel.Warning, // lease broke/breaking
                HttpStatusCode.PreconditionFailed => LogLevel.Warning, // lease lost
                _ => LogLevel.Error,
            };

#pragma warning disable CA1873 // Avoid potentially expensive logging
            LogFailureRenewingLease(
                logLevel,
                _blobClient.Uri.GetLeftPart(UriPartial.Path),
                ex.Status,
                ex.ErrorCode,
                ex
            );
#pragma warning restore CA1873 // Avoid potentially expensive logging
        }
        finally
        {
            if (!success)
            {
                // give up the lease in our state to avoid being stuck in a bad state
                await ResetLeadershipAsync().ConfigureAwait(false);
            }
        }

        return success;
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (string.IsNullOrEmpty(_currentLeaseId))
        {
            LogNoLeaseToRelease();
            return;
        }

        Debug.Assert(_blobClient != null);

        try
        {
            var leaseClient = _blobClient.GetBlobLeaseClient(_currentLeaseId);
            await leaseClient.ReleaseAsync().ConfigureAwait(false);

            LogLeaseReleased(_blobClient.Uri.GetLeftPart(UriPartial.Path));
        }
        catch (Azure.RequestFailedException ex)
        {
            // this is unexpected since we should own the lease.
            // Log as a warning and give up our leadership.
            var logLevel = (HttpStatusCode)ex.Status switch
            {
                HttpStatusCode.NotFound => LogLevel.Warning, // blob deleted?
                HttpStatusCode.Conflict => LogLevel.Warning, // lease broke/breaking
                HttpStatusCode.PreconditionFailed => LogLevel.Warning, // lease lost
                _ => LogLevel.Error,
            };

#pragma warning disable CA1873 // Avoid potentially expensive logging
            LogFailureReleasingLease(
                logLevel,
                _blobClient.Uri.GetLeftPart(UriPartial.Path),
                ex.Status,
                ex.ErrorCode
            );
#pragma warning restore CA1873 // Avoid potentially expensive logging
        }
        finally
        {
            // always give up local leadership state even if release failed, since we don't
            // want to be stuck in a bad state where we think we are the leader but are not.
            await ResetLeadershipAsync().ConfigureAwait(false);
        }
    }

    protected override ValueTask ResetLeadershipAsync()
    {
        _currentLeaseId = null;
        _blobClient = null;
        return new ValueTask();
    }

    private async Task<BlobClient> CreateBlobClientAsync(CancellationToken cancellationToken)
    {
        if (_settings.BlobClientFactory != null)
        {
            if (!string.IsNullOrEmpty(_settings.ConnectionString))
            {
                LogIgnoringConnectionStringBecauseFactoryIsSet();
            }

            return await _settings
                .BlobClientFactory(_settings, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(_settings.ConnectionString))
        {
            var bsc = new BlobServiceClient(_settings.ConnectionString);
            return await BlobStorageServiceBuilderExtensions
                .CreateBlobClient(bsc, _settings, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "Either BlobClientFactory or ConnectionString must be specified in settings."
        );
    }

    private async Task EnsureBlobExistsAsync(
        BlobClient blobClient,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await blobClient
                .UploadAsync(
                    new BinaryData(_settings.InstanceId),
                    overwrite: false,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            LogCreatedBlob(blobClient.Uri.GetLeftPart(UriPartial.Path));
        }
        catch (Azure.RequestFailedException ex)
            when (ex.ErrorCode == BlobErrorCode.BlobAlreadyExists)
        {
            // Okay.
        }
        catch (Azure.RequestFailedException ex)
        {
            LogFailureCreatingBlob(
                blobClient.Uri.GetLeftPart(UriPartial.Path),
                ex.Status,
                ex.ErrorCode
            );
        }
    }

    [LoggerMessage(LogLevel.Information, "Lease already acquired on {BlobUrl}.")]
    partial void LogLeaseAlreadyAcquired(string blobUrl);

    [LoggerMessage(LogLevel.Debug, "Lease acquired on {BlobUrl}: {LeaseId}.")]
    partial void LogAcquiredLeaseWithIdLeaseId(string blobUrl, string leaseId);

    [LoggerMessage("Failure acquiring lease on {BlobUrl}: {Status} - {ErrorCode}.")]
    partial void LogFailureAcquiringLease(
        LogLevel level,
        string blobUrl,
        int status,
        string? errorCode
    );

    [LoggerMessage(LogLevel.Information, "No lease to renew.")]
    partial void LogNoCurrentLeaseToRenew();

    [LoggerMessage(LogLevel.Debug, "Lease renewed on {BlobUrl}: {LeaseId}.")]
    partial void LogLeaseRenewed(string blobUrl, string leaseId);

    [LoggerMessage("Failure renewing lease on {BlobUrl}: {Status} - {ErrorCode}.")]
    partial void LogFailureRenewingLease(
        LogLevel logLevel,
        string blobUrl,
        int status,
        string? errorCode,
        Exception exception
    );

    [LoggerMessage(LogLevel.Information, "No lease to release.")]
    partial void LogNoLeaseToRelease();

    [LoggerMessage(LogLevel.Debug, "Lease released on {BlobUrl}.")]
    partial void LogLeaseReleased(string blobUrl);

    [LoggerMessage("Failure releasing lease on {BlobUrl}: {Status} - {ErrorCode}.")]
    partial void LogFailureReleasingLease(
        LogLevel logLevel,
        string blobUrl,
        int status,
        string? errorCode
    );

    [LoggerMessage(
        LogLevel.Warning,
        "Ignoring ConnectionString, ContainerName, and BlobName because BlobClientFactory is set."
    )]
    partial void LogIgnoringConnectionStringBecauseFactoryIsSet();

    [LoggerMessage(LogLevel.Information, "Created blob: {BlobUrl}.")]
    partial void LogCreatedBlob(string blobUrl);

    [LoggerMessage(LogLevel.Warning, "Failure creating blob: {BlobUrl}: {Status} - {ErrorCode}.")]
    partial void LogFailureCreatingBlob(string blobUrl, int status, string? errorCode);
}
