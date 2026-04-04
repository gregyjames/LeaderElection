using System.Text.Json;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace LeaderElection.S3;

public sealed partial class S3LeaderElection : LeaderElectionBase<S3Settings>
{
    private readonly IMinioClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private string? _lastEtag;

    public S3LeaderElection(
        S3Settings settings,
        IMinioClient client,
        ILogger<S3LeaderElection>? logger = null,
        TimeProvider? timeProvider = null
    )
        : base(settings ?? throw new ArgumentNullException(nameof(settings)), logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_lastEtag != null)
        {
            LogLeaseAlreadyAcquired(_settings.BucketName, _settings.ObjectKey);
            return false;
        }

        // Try to read the current lease...
        var (currentEtag, currentLease) = await TryReadLeaseAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        if (
            currentLease != null
            && currentLease.LeaseUntilUtc > now
            && currentLease.HolderId != _settings.InstanceId
        )
        {
            // Lease is still valid and held by someone else
            LogFailureAcquiringLease(
                LogLevel.Debug,
                _settings.BucketName,
                _settings.ObjectKey,
                "Lease is currently held by another instance."
            );

            return false; // failed to acquire leadership
        }

        // Try to acquire the lease by writing a new LeaseRecord using the
        // currentEtag. If currentEtag is null, it will create the object
        // if it doesn't exist.
        var leaseRecord = new LeaseRecord
        {
            HolderId = _settings.InstanceId,
            LeaseUntilUtc = now.Add(_settings.LeaseDuration),
        };
        try
        {
            _lastEtag = await WriteLeaseAsync(leaseRecord, currentEtag, cancellationToken)
                .ConfigureAwait(false);

            // success!
            LogAcquiredLease(_settings.BucketName, _settings.ObjectKey, _settings.InstanceId);
            return true;
        }
        catch (PreconditionFailedException)
        {
            LogFailureAcquiringLease(
                LogLevel.Debug,
                _settings.BucketName,
                _settings.ObjectKey,
                "ETag mismatch, likely due to another instance acquiring leadership concurrently."
            );
            return false;
        }
        catch (MinioException ex)
        {
            // failure due to an error accessing S3 (e.g., network issue,
            // permissions issue, etc.)
            LogFailureAcquiringLease(
                LogLevel.Error,
                _settings.BucketName,
                _settings.ObjectKey,
                ex.GetType().FullName + ": " + ex.Message
            );
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_lastEtag == null)
        {
            LogNoCurrentLeaseToRenew();
            return false;
        }

        // To renew the lease, we write a new record with the same InstanceId and
        // a new expiry time, but we must include the ETag of the current record to
        // ensure we only succeed if the record hasn't been modified by another instance.
        // If we succeed, we have effectively renewed the lease. If we fail with a
        // PreconditionFailed, it likely means we lost leadership due to another instance
        // acquiring leadership, but we treat it as a failure to renew since we no longer
        // hold the lease. For other errors, we log them and treat it as a failure to
        // renew; if it's a transient error, someone will become the leader when the lease
        // naturally expires.
        var success = false;
        var now = _timeProvider.GetUtcNow();
        var leaseRecord = new LeaseRecord
        {
            HolderId = _settings.InstanceId,
            LeaseUntilUtc = now.Add(_settings.LeaseDuration),
        };

        try
        {
            _lastEtag = await WriteLeaseAsync(leaseRecord, _lastEtag, cancellationToken)
                .ConfigureAwait(false);

            success = true;
            LogLeaseRenewed(_settings.BucketName, _settings.ObjectKey, _settings.InstanceId);
        }
        catch (PreconditionFailedException)
        {
            // Lost leadership because the record was modified by another instance
            // (e.g., another instance acquired leadership).
            // This should not normally happen since the lease record should only be
            // modified by the current holder, but we handle it just in case.
            LogFailureRenewingLease(
                LogLevel.Information,
                _settings.BucketName,
                _settings.ObjectKey,
                "ETag mismatch, likely due to another instance acquiring leadership concurrently."
            );
        }
        catch (MinioException ex)
        {
            // Lost leadership due to an error accessing S3 (e.g., network issue,
            // permissions issue, etc.)
            LogFailureRenewingLease(
                LogLevel.Error,
                _settings.BucketName,
                _settings.ObjectKey,
                ex.GetType().FullName + ": " + ex.Message
            );
        }
        finally
        {
            if (!success)
            {
                // Clear the ETag regardless of the error to avoid being stuck in
                // a state where we think we have the lease when we don't.
                _lastEtag = null;
            }
        }
        return success;
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (_lastEtag == null)
        {
            LogNoCurrentLeaseToRelease();
            return;
        }

        // To release the lease, we write an empty record with an old expiry time, but we
        // must include the ETag of the current record to ensure we only succeed if the
        // record hasn't been modified by another instance (e.g., another instance acquired
        // leadership). If we succeed, we have effectively released the lease. If we fail with
        // a PreconditionFailed, it likely means we lost leadership due to another instance
        // acquiring leadership, but we treat it as a successful release since we no longer
        // hold the lease. For other errors, we log them but there's not much else we can do;
        // if it's a transient error, someone will become the leader when the lease naturally
        // expires.
        var emptyLease = new LeaseRecord();
        try
        {
            _ = await PutLeaseAsync(emptyLease, _lastEtag, CancellationToken.None)
                .ConfigureAwait(false);

            _lastEtag = null;
            LogLeaseReleased(_settings.BucketName, _settings.ObjectKey, _settings.InstanceId);
        }
        catch (ObjectNotFoundException)
        {
            LogFailureReleasingLease(
                LogLevel.Information,
                _settings.BucketName,
                _settings.ObjectKey,
                "Object not found, likely deleted."
            );
        }
        catch (PreconditionFailedException)
        {
            // This should not normally happen, but not a problem if it does.
            LogFailureReleasingLease(
                LogLevel.Information,
                _settings.BucketName,
                _settings.ObjectKey,
                "ETag mismatch, likely due to another instance acquiring leadership concurrently."
            );
        }
        catch (MinioException ex)
        {
            // failure due to an error accessing S3 (e.g., network issue,
            // permissions issue, etc.)
            LogFailureReleasingLease(
                LogLevel.Error,
                _settings.BucketName,
                _settings.ObjectKey,
                ex.GetType().FullName + ": " + ex.Message
            );
        }
        finally
        {
            // Clear the ETag regardless of success to avoid being stuck in
            // a state where we think we have the lease when we don't.
            _lastEtag = null;
        }
    }

    private async Task<string> WriteLeaseAsync(
        LeaseRecord leaseRecord,
        string? currentEtag,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await PutLeaseAsync(leaseRecord, currentEtag, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ObjectNotFoundException) when (currentEtag != null)
        {
            // Try again without an ETag, i.e., create it if it doesn't exist.
            return await PutLeaseAsync(leaseRecord, null, cancellationToken).ConfigureAwait(false);
        }
    }

    // Attempts to read the lease record from S3:
    // - If the object doesn't exist, returns (null, null).
    // - If the object exists but can't be deserialized, returns (etag, null) and logs a warning.
    // - If the object exists and is successfully deserialized, returns (etag, leaseRecord).
    // - Any exceptions thrown by the Minio client (e.g., due to permissions issues, network issues, etc.)
    //   are caught and logged, and it returns (null, null) to indicate no valid lease record could be read.
    private async Task<(string? currentEtag, LeaseRecord? currentLease)> TryReadLeaseAsync(
        CancellationToken cancellationToken
    )
    {
        string? currentEtag = null;
        LeaseRecord? currentLease = null;
        try
        {
            using var memoryStream = new MemoryStream();
            var stat = await _client
                .GetObjectAsync(
                    new GetObjectArgs()
                        .WithBucket(_settings.BucketName)
                        .WithObject(_settings.ObjectKey)
                        .WithCallbackStream(stream => stream.CopyTo(memoryStream)),
                    cancellationToken
                )
                .ConfigureAwait(false);

            // It exists.
            currentEtag = NormalizeETag(stat.ETag);

            // Deserialize the content...
            try
            {
                memoryStream.Position = 0;
                currentLease = await JsonSerializer
                    .DeserializeAsync<LeaseRecord>(memoryStream, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                // Appears to be a valid record
            }
            catch (JsonException ex)
            {
                // If we can't deserialize the content, we treat it as if there was no
                // valid lease record, i.e., we'll overwrite it and acquire leadership.
                // This is a bit of a safety valve in case the content gets corrupted somehow,
                // but we log it just in case.
                LogFailedToDeserializeLeaseRecord(ex, _settings.BucketName, _settings.ObjectKey);
            }
        }
        catch (ObjectNotFoundException)
        {
            // Okay. We'll create it when we acquire the lease.
        }
        catch (MinioException)
        {
            // ignore other Minio exceptions (e.g., access denied) and just treat it
            // as if there was no current lease, which means we'll try to acquire it.
            // If it's an access issue or something else that would prevent us from
            // acquiring the lease, that will just cause the PutLeaseAsync call to fail
            // and we'll log it there.
        }

        return (currentEtag, currentLease);
    }

    // Writes the lease record to S3 with the specified ETag for concurrency control.
    // - If currentEtag is null, it will attempt to create the object if it doesn't exist.
    // - Returns the new ETag if successful.
    private async Task<string> PutLeaseAsync(
        LeaseRecord record,
        string? etag,
        CancellationToken token
    )
    {
        using var stream = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(record, _jsonOptions)
        );

        var request = new PutObjectArgs()
            .WithBucket(_settings.BucketName)
            .WithObject(_settings.ObjectKey)
            .WithStreamData(stream)
            .WithObjectSize(stream.Length)
            .WithContentType("application/json");

        if (!string.IsNullOrEmpty(etag))
        {
            request.WithMatchETag(etag);
        }
        else
        {
            request.WithNotMatchETag("*"); // "if does not exist"
        }

        var response = await _client.PutObjectAsync(request, token).ConfigureAwait(false);
        var newEtag = NormalizeETag(response.Etag);

        return string.IsNullOrEmpty(newEtag)
            ? throw new InvalidOperationException(
                "PutObjectAsync succeeded but no ETag was returned."
            )
            : newEtag;
    }

    // Minio's ETag handling can be inconsistent, so we normalize it by adding
    // quotes if missing (standard etags are quoted).
    // See https://github.com/minio/minio-dotnet/issues/1038
    private static string? NormalizeETag(string? etag) =>
        string.IsNullOrEmpty(etag) ? etag
        : etag[0] == '"' && etag[^1] == '"' ? etag // already quoted
        : $"\"{etag}\""; // add quotes

    [LoggerMessage(LogLevel.Information, "Lease already acquired for {BucketName}/{ObjectKey}.")]
    partial void LogLeaseAlreadyAcquired(string bucketName, string objectKey);

    [LoggerMessage(
        LogLevel.Debug,
        "Lease acquired on {BucketName}/{ObjectKey} by instance {InstanceId}."
    )]
    partial void LogAcquiredLease(string bucketName, string objectKey, string instanceId);

    [LoggerMessage("Failure acquiring lease on {BucketName}/{ObjectKey}: {Reason}")]
    partial void LogFailureAcquiringLease(
        LogLevel logLevel,
        string bucketName,
        string objectKey,
        string reason
    );

    [LoggerMessage(LogLevel.Information, "No current lease to renew.")]
    partial void LogNoCurrentLeaseToRenew();

    [LoggerMessage(
        LogLevel.Debug,
        "Lease renewed on {BucketName}/{ObjectKey} by instance {InstanceId}."
    )]
    partial void LogLeaseRenewed(string bucketName, string objectKey, string instanceId);

    [LoggerMessage("Failure renewing lease for {BucketName}/{ObjectKey}: {Reason}")]
    partial void LogFailureRenewingLease(
        LogLevel logLevel,
        string bucketName,
        string objectKey,
        string reason
    );

    [LoggerMessage(LogLevel.Information, "No current lease to release.")]
    partial void LogNoCurrentLeaseToRelease();

    [LoggerMessage(
        LogLevel.Debug,
        "Lease released for {BucketName}/{ObjectKey} by instance {InstanceId}."
    )]
    partial void LogLeaseReleased(string bucketName, string objectKey, string instanceId);

    [LoggerMessage("Failure releasing lease for {BucketName}/{ObjectKey}: {Reason}")]
    partial void LogFailureReleasingLease(
        LogLevel logLevel,
        string bucketName,
        string objectKey,
        string reason
    );

    [LoggerMessage(
        LogLevel.Error,
        "Failed to deserialize lease record for {BucketName}/{ObjectKey}."
    )]
    partial void LogFailedToDeserializeLeaseRecord(
        Exception exception,
        string bucketName,
        string objectKey
    );
}
