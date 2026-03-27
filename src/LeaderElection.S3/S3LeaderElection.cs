using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace LeaderElection.S3;

public sealed class S3LeaderElection : LeaderElectionBase<S3Settings>
{
    private readonly IMinioClient _client;
    private readonly S3Settings _options;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private string? _lastEtag;

    public S3LeaderElection(
        IMinioClient client,
        IOptions<S3Settings> options,
        ILogger<S3LeaderElection> logger) : base(options.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
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
                    .WithObject(_options.ObjectKey), cancellationToken).ConfigureAwait(false);
                currentEtag = NormalizeETag(stat.ETag);

                var memoryStream = new MemoryStream();
                await _client.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(_options.ObjectKey)
                    .WithCallbackStream((stream) => stream.CopyTo(memoryStream)), cancellationToken).ConfigureAwait(false);

                memoryStream.Position = 0;
                currentLease = await JsonSerializer.DeserializeAsync<LeaseRecord>(memoryStream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await memoryStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectNotFoundException)
            {
                logger.LogDebug("Object not found, Trying to create it.");
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

            _lastEtag = await PutLeaseAsync(leaseRecord, headers, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ErrorResponseException ex) when (ex.Response.Code is "PreconditionFailed" or "AccessDenied")
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring S3 leadership");
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(_lastEtag))
            {
                return await TryAcquireLeadershipInternalAsync(cancellationToken).ConfigureAwait(false);
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
                    .WithObject(_options.ObjectKey), cancellationToken).ConfigureAwait(false);
                
                var etag = NormalizeETag(stat.ETag);
                if (etag != _lastEtag)
                {
                    logger.LogWarning("S3 ETag mismatch during renewal. Expected {Expected}, got {Actual}", _lastEtag, etag);
                    return false;
                }

                var memoryStream = new MemoryStream();
                await _client.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(_options.ObjectKey)
                    .WithCallbackStream((stream) => stream.CopyTo(memoryStream)), cancellationToken).ConfigureAwait(false);

                memoryStream.Position = 0;
                var currentLease =
                    await JsonSerializer.DeserializeAsync<LeaseRecord>(memoryStream, _jsonOptions,
                        cancellationToken).ConfigureAwait(false);
                if (currentLease?.HolderId != _options.InstanceId)
                {
                    return false;
                }
                
                await memoryStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }

            _lastEtag = await PutLeaseAsync(leaseRecord, new Dictionary<string, string> { ["If-Match"] = _lastEtag }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ErrorResponseException ex) when (ex.Response.Code is "PreconditionFailed" or "AccessDenied")
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error renewing S3 leadership");
            return false;
        }
    }

    protected override async Task ReleaseLeadershipAsync()
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

            await PutLeaseAsync(leaseRecord, new Dictionary<string, string> { ["If-Match"] = _lastEtag }, default).ConfigureAwait(false);
            logger.LogInformation("Leadership released for instance {InstanceId}", _options.InstanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing S3 leadership");
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

        var response = await _client.PutObjectAsync(request, token).ConfigureAwait(false);
        var etag = NormalizeETag(response?.Etag);

        if (string.IsNullOrEmpty(etag))
        {
            throw new InvalidOperationException("PutObjectAsync succeeded but no ETag was returned.");
        }

        return etag;
    }

    // Minio's ETag handling can be inconsistent, so we normalize it by adding
    // quotes if missing (standard etags are quoted).
    // See https://github.com/minio/minio-dotnet/issues/1038
    private static string? NormalizeETag(string? etag) =>
        string.IsNullOrEmpty(etag) ? etag
        : etag[0] == '"' && etag[^1] == '"' ? etag // already quoted
        : $"\"{etag}\""; // add quotes
}