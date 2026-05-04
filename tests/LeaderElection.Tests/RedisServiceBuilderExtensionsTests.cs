using LeaderElection.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace LeaderElection.Tests;

public sealed class RedisServiceBuilderExtensionsTests
{
    // Settings with all properties set to non-default values.
    private readonly RedisSettings settings = new()
    {
        Database = 22,
        LockKey = "test-lock",
        LockExpiry = TimeSpan.FromSeconds(10),
        InstanceId = "foo",
        RenewInterval = TimeSpan.FromSeconds(2),
        RetryBackoffFactor = 1.5,
        RetryJitter = 0.15,
        MaxRetryInterval = TimeSpan.FromSeconds(100),
        EnableGracefulShutdown = false,
    };

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddOptionsCorrectlyWhenUsingDirectSettings(string? serviceKey)
    {
        // Arrange

        // Act
        await using var sp = new ServiceCollection()
            .AddRedisLeaderElection(b => b.WithSettings(settings), serviceKey: serviceKey)
            .BuildServiceProvider();

        // Assert
        var actualSettings = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>()
            .Get(serviceKey);
        actualSettings.ConnectionMultiplexerFactory.Should().NotBeNull();
        actualSettings.ConnectionMultiplexerFactory = null;
        actualSettings.Should().BeEquivalentTo(settings);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddOptionsCorrectlyWhenUsingConfiguration(string? serviceKey)
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["LeaderElection:Redis:Database"] = settings.Database.ToString(),
                    ["LeaderElection:Redis:LockExpiry"] = settings.LockExpiry.ToString(),
                    ["LeaderElection:Redis:LockKey"] = settings.LockKey,
                    // ["LeaderElection:Base:InstanceId"] = settings.InstanceId,
                    ["LeaderElection:Base:RenewInterval"] = settings.RenewInterval.ToString(),
                    ["LeaderElection:Base:RetryInterval"] = settings.RetryInterval.ToString(),
                    ["LeaderElection:Base:RetryBackoffFactor"] =
                        settings.RetryBackoffFactor.ToString(),
                    ["LeaderElection:Base:RetryJitter"] = settings.RetryJitter.ToString(),
                    ["LeaderElection:Base:MaxRetryInterval"] = settings.MaxRetryInterval.ToString(),
                    // ["LeaderElection:Base:EnableGracefulShutdown"] = settings.EnableGracefulShutdown.ToString(),
                }
            )
            .Build();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(config)
            .AddRedisLeaderElection(
                builder =>
                    builder
                        .WithConfiguration("LeaderElection:Redis")
                        .WithConfiguration(config.GetSection("LeaderElection:Base"))
                        .WithInstanceId(settings.InstanceId)
                        .WithSettings(s =>
                            s.EnableGracefulShutdown = settings.EnableGracefulShutdown
                        ),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var actualSettings = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>()
            .Get(serviceKey);
        actualSettings.ConnectionMultiplexerFactory.Should().NotBeNull();
        actualSettings.ConnectionMultiplexerFactory = null;
        actualSettings.Should().BeEquivalentTo(settings);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("foo", false)]
    [InlineData(null, false)]
    public async Task ShouldSetRegisteredConnectionMultiplexerFactory(
        string? serviceKey,
        bool useKeyedCM
    )
    {
        // Arrange
        var defaultSettings = new RedisSettings();

        var mockCM = Mock.Of<IConnectionMultiplexer>();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton(useKeyedCM ? serviceKey : null, mockCM)
            .AddRedisLeaderElection(builder => builder.WithSettings(defaultSettings), serviceKey)
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>().Get(serviceKey);
        options.ConnectionMultiplexerFactory.Should().NotBeNull();
        var actualCM = options.ConnectionMultiplexerFactory(defaultSettings);
        actualCM.Should().BeSameAs(mockCM);

        var leaderElection = sp.GetRequiredKeyedService<RedisLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingCMInstance(string? serviceKey)
    {
        // Arrange
        var mockCM = Mock.Of<IConnectionMultiplexer>();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddRedisLeaderElection(
                builder => builder.WithConnectionMultiplexer(mockCM),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>().Get(serviceKey);
        options.ConnectionMultiplexerFactory.Should().NotBeNull();
        var actualCM = options.ConnectionMultiplexerFactory(settings);
        actualCM.Should().BeSameAs(mockCM);

        var leaderElection = sp.GetRequiredKeyedService<RedisLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingCustomCMFactory(string? serviceKey)
    {
        // Arrange
        var mockCM = Mock.Of<IConnectionMultiplexer>();

        // Act
#pragma warning disable CA2025 // Do not pass 'IDisposable' instances into unawaited tasks
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddRedisLeaderElection(
                builder => builder.WithConnectionMultiplexerFactory((_, _) => mockCM),
                serviceKey
            )
            .BuildServiceProvider();
#pragma warning restore CA2025 // Do not pass 'IDisposable' instances into unawaited tasks

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>().Get(serviceKey);
        options.ConnectionMultiplexerFactory.Should().NotBeNull();
        var actualCM = options.ConnectionMultiplexerFactory(settings);
        actualCM.Should().BeSameAs(mockCM);

        var leaderElection = sp.GetRequiredKeyedService<RedisLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }
}
