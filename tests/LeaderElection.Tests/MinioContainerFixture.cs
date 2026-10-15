using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Minio;

namespace LeaderElection.Tests;

/// <summary>
/// Defines a "collection fixture" which ensures that a single S3-compatible container
/// is shared across all tests that require it.
/// </summary>
[CollectionDefinition("Minio Container")]
public sealed class MinioContainerCollectionFixture : ICollectionFixture<MinioContainerFixture> { }

/// <summary>
/// A Xunit fixture that manages the lifecycle of a temporary S3-compatible container
/// for testing purposes.
/// </summary>
/// <remarks>
/// The container runs RustFS (https://rustfs.com), since the MinIO server images are no
/// longer published to public registries. The MinIO .NET SDK is still used as the S3
/// client, as it works with any S3-compatible server.
/// </remarks>
public sealed class MinioContainerFixture : IAsyncLifetime
{
    private const string Image = "rustfs/rustfs:latest";
    private const int S3Port = 9000;

    private IContainer? _container;

    public static string AccessKey => "minioadmin";
    public static string SecretKey => "minioadmin";

    public string Endpoint =>
        _container is null
            ? throw new InvalidOperationException("S3 container is not initialized.")
            : $"{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}";

    public IMinioClient CreateClient()
    {
        if (_container == null)
        {
            throw new InvalidOperationException("S3 container is not initialized.");
        }

        var client = new MinioClient();
        var iclient = client
            .WithEndpoint(_container.Hostname, _container.GetMappedPublicPort(S3Port))
            .WithCredentials(AccessKey, SecretKey)
            .WithSSL(false)
            .Build();
        Debug.Assert(
            object.ReferenceEquals(iclient, client),
            "Build returns the disposable instance."
        );
        return client;
    }

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(S3Port, true)
            .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request =>
                        request.ForPort(S3Port).ForPath("/health")
                    )
            )
            .Build();

        await _container.StartAsync().ConfigureAwait(false);

        // The /health endpoint can respond before the S3 API is fully ready (especially
        // under load), so also wait until an authenticated S3 request succeeds.
        using var client = CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                _ = await client.ListBucketsAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
