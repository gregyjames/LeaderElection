using System.Diagnostics.CodeAnalysis;
using Testcontainers.PostgreSql;

namespace LeaderElection.Tests;

/// <summary>
/// Defines a "collection fixture" which ensures that a single PostgreSQL container is shared
/// across all tests that require it.
/// </summary>
[CollectionDefinition("PostgreSQL Container")]
[SuppressMessage("Maintainability", "CA1515:Consider making public types internal")]
public sealed class PostgresContainerCollectionFixture : ICollectionFixture<PostgresContainerFixture> { }

/// <summary>
/// An Xunit fixture that manages the lifecycle of a temporary PostgreSQL container for
/// testing purposes.
/// </summary>
#pragma warning disable CA1515
public sealed class PostgresContainerFixture : IAsyncLifetime
#pragma warning restore CA1515
{
    private PostgreSqlContainer _postgresContainer = default!;

    /// <summary>
    /// Gets the connection string for the PostgreSQL container.
    /// </summary>
    public string ConnectionString =>
        _postgresContainer?.GetConnectionString()
        ?? throw new InvalidOperationException(
            "PostgreSQL container is not initialized. Ensure InitializeAsync has been called."
        );

    public async ValueTask InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder(image: "postgres:15-alpine")
            .Build();

        await _postgresContainer.StartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgresContainer != null)
        {
            await _postgresContainer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
