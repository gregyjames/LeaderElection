using Azure.Storage.Blobs;
using Testcontainers.Azurite;

namespace LeaderElection.Tests;

/// <summary>
/// Defines a "collection fixture" which ensures that a single Azurite container is shared
/// across all tests that require it.
/// </summary>
/// <remarks>
/// Apply the <c>[Collection("Azurite Container")]</c> attribute to any test class that
/// requires access to the singleton <see cref="AzuriteContainerFixture"/>.
/// </remarks>
[CollectionDefinition("Azurite Container")]
public sealed class AzuriteContainerCollectionFixture : ICollectionFixture<AzuriteContainerFixture> { }

/// <summary>
/// An Xunit fixture that manages the lifecycle of a temporary Azurite container for
/// testing purposes.
/// </summary>
public sealed class AzuriteContainerFixture : IAsyncLifetime
{
    private AzuriteContainer? _azuriteContainer;

    /// <summary>
    /// Gets the connection string for the Azurite container.
    /// </summary>
    public string ConnectionString =>
        _azuriteContainer?.GetConnectionString()
        ?? throw new InvalidOperationException(
            "Azurite container is not initialized. Ensure InitializeAsync has been called."
        );

    /// <summary>
    /// Gets a new <see cref="BlobServiceClient"/> configured to connect to
    /// the Azurite container.
    /// </summary>
    public BlobServiceClient BlobServiceClient => new(ConnectionString);

    public async ValueTask InitializeAsync()
    {
        // The "--loose" and "--skipApiVersionCheck" flags are used to allow Azurite
        // (which tends to lag behind a bit) to work with the latest Azure Storage SDK.
        _azuriteContainer = new AzuriteBuilder(
            image: "mcr.microsoft.com/azure-storage/azurite:latest"
        )
            .WithCommand("--loose", "--skipApiVersionCheck")
            .Build();

        await _azuriteContainer.StartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_azuriteContainer != null)
        {
            await _azuriteContainer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
