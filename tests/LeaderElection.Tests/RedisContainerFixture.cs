using StackExchange.Redis;
using Testcontainers.Redis;

namespace LeaderElection.Tests;

/// <summary>
/// Defines a "collection fixture" which ensures that a single Redis container is shared
/// across all tests that require it.
/// </summary>
/// <remarks>
/// Apply the <c>[Collection("Redis Container")]</c> attribute to any test class that
/// requires access to the singleton <see cref="RedisContainerFixture"/>.
/// </remarks>
[CollectionDefinition("Redis Container")]
public sealed class RedisContainerCollectionFixture : ICollectionFixture<RedisContainerFixture> { }

/// <summary>
/// A Xunit fixture that manages the lifecycle of a temporary Redis container for
/// testing purposes.
/// </summary>
public sealed class RedisContainerFixture : IAsyncLifetime
{
    private RedisContainer _redisContainer = default!;
    private ConnectionMultiplexer _connectionMultiplexer = null!;

    /// <summary>
    /// Gets the connection string for the Redis container.
    /// </summary>
    public string ConnectionString =>
        _redisContainer?.GetConnectionString()
        ?? throw new InvalidOperationException(
            "Redis container is not initialized. Ensure InitializeAsync has been called."
        );

    /// <summary>
    /// Provides access to the container's Redis connection multiplexer.
    /// </summary>
    public IConnectionMultiplexer ConnectionMultiplexer =>
        _connectionMultiplexer
        ?? throw new InvalidOperationException(
            "ConnectionMultiplexer is not initialized. Ensure InitializeAsync has been called."
        );

    public async ValueTask InitializeAsync()
    {
        _redisContainer = new RedisBuilder(image: "redis:7-alpine").Build();
        await _redisContainer.StartAsync().ConfigureAwait(false);

        var connectionString = _redisContainer.GetConnectionString();
        _connectionMultiplexer = await StackExchange
            .Redis.ConnectionMultiplexer.ConnectAsync(connectionString)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionMultiplexer.CloseAsync().ConfigureAwait(false);
        await _connectionMultiplexer.DisposeAsync().ConfigureAwait(false);

        await _redisContainer.DisposeAsync().ConfigureAwait(false);
    }
}
