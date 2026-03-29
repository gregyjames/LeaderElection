using System.Diagnostics;
using Minio;
using Testcontainers.Minio;

namespace LeaderElection.Tests;

/// <summary>
/// Defines a "collection fixture" which ensures that a single Minio container is shared
/// across all tests that require it.
/// </summary>
[CollectionDefinition("Minio Container")]
public sealed class MinioContainerCollectionFixture : ICollectionFixture<MinioContainerFixture> { }

/// <summary>
/// A Xunit fixture that manages the lifecycle of a temporary Minio container for
/// testing purposes.
/// </summary>
public sealed class MinioContainerFixture : IAsyncLifetime
{
    private MinioContainer? _minioContainer;

    public static string AccessKey => "minioadmin";
    public static string SecretKey => "minioadmin";
    public string Endpoint => _minioContainer?.GetConnectionString() ?? throw new InvalidOperationException("Minio container is not initialized.");

    public IMinioClient CreateClient()
    {
        if (_minioContainer == null)
        {
            throw new InvalidOperationException("Minio container is not initialized.");
        }

        var client = new MinioClient();
        var iclient = client
            .WithEndpoint(_minioContainer.Hostname, _minioContainer.GetMappedPublicPort(9000))
            .WithCredentials(AccessKey, SecretKey)
            .WithSSL(false)
            .Build();
        Debug.Assert(object.ReferenceEquals(iclient, client), "Build returns the disposable instance.");
        return client;
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
        {
            await _minioContainer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
