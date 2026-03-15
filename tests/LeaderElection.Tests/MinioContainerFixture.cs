using Minio;
using Testcontainers.Minio;

namespace LeaderElection.Tests;

/// <summary>
/// Defines a "collection fixture" which ensures that a single Minio container is shared
/// across all tests that require it.
/// </summary>
[CollectionDefinition("Minio Container")]
public sealed class MinioContainerCollection : ICollectionFixture<MinioContainerFixture> { }

/// <summary>
/// An Xunit fixture that manages the lifecycle of a temporary Minio container for
/// testing purposes.
/// </summary>
public sealed class MinioContainerFixture : IAsyncLifetime
{
    private MinioContainer _minioContainer = default!;

    public string AccessKey => "minioadmin";
    public string SecretKey => "minioadmin";
    public string Endpoint => _minioContainer?.GetConnectionString() ?? throw new InvalidOperationException("Minio container is not initialized.");

    public IMinioClient CreateClient()
    {
        return new MinioClient()
            .WithEndpoint(_minioContainer.Hostname, _minioContainer.GetMappedPublicPort(9000))
            .WithCredentials(AccessKey, SecretKey)
            .WithSSL(false)
            .Build();
    }

    public async ValueTask InitializeAsync()
    {
        _minioContainer = new MinioBuilder(image: "minio/minio")
            .WithUsername(AccessKey)
            .WithPassword(SecretKey)
            .Build();

        await _minioContainer.StartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_minioContainer != null)
            await _minioContainer.DisposeAsync().ConfigureAwait(false);
    }
}
