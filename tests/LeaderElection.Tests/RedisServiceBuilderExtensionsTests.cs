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
        Host = "foo.localhost",
        Port = 101,
        Password = "pass",
        Database = 22,
        LockKey = "test-lock",
        LockExpiry = TimeSpan.FromSeconds(10),
        InstanceId = "foo",
        RenewInterval = TimeSpan.FromSeconds(2),
        RetryInterval = TimeSpan.FromSeconds(1),
        MaxRetryAttempts = 300,
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
                    ["LeaderElection:Redis:Host"] = settings.Host,
                    ["LeaderElection:Redis:Port"] = settings.Port.ToString(),
                    ["LeaderElection:Redis:Password"] = settings.Password,
                    ["LeaderElection:Redis:Database"] = settings.Database.ToString(),
                    ["LeaderElection:Redis:LockExpiry"] = settings.LockExpiry.ToString(),
                    ["LeaderElection:Redis:LockKey"] = settings.LockKey,
                    // ["LeaderElection:Base:InstanceId"] = settings.InstanceId,
                    ["LeaderElection:Base:RenewInterval"] = settings.RenewInterval.ToString(),
                    ["LeaderElection:Base:RetryInterval"] = settings.RetryInterval.ToString(),
                    ["LeaderElection:Base:MaxRetryAttempts"] = settings.MaxRetryAttempts.ToString(),
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
        var actualSettings = sp.GetRequiredService<IOptionsSnapshot<RedisSettings>>()
            .Get(serviceKey);
        actualSettings.Should().BeEquivalentTo(settings);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("foo", false)]
    [InlineData(null, false)]
    public async Task ShouldSetDefaultConnectionMultiplexerFactoryWhenHostIsEmpty(
        string? serviceKey,
        bool useKeyedCM
    )
    {
        // Arrange
        var noHostSettings = new RedisSettings
        {
            Host = null, // use CM from DI
        };

        var mockCM = Mock.Of<IConnectionMultiplexer>();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton(useKeyedCM ? serviceKey : null, mockCM)
            .AddRedisLeaderElection(builder => builder.WithSettings(noHostSettings), serviceKey)
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>().Get(serviceKey);
        options.ConnectionMultiplexerFactory.Should().NotBeNull();
        var actualCM = await options.ConnectionMultiplexerFactory(
            noHostSettings,
            TestContext.Current.CancellationToken
        );
        actualCM.Should().BeSameAs(mockCM);

        var leaderElection = sp.GetRequiredKeyedService<RedisLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("foo", false)]
    [InlineData(null, false)]
    public async Task ShouldUseRegisteredConnectionMultiplexerWhenTold(
        string? serviceKey,
        bool useKeyedCM
    )
    {
        // Arrange
        // ensure host is set to confirm that factory is used instead of host/port
        settings.Host = "foo.localhost";

        var mockCM = Mock.Of<IConnectionMultiplexer>();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton(useKeyedCM ? serviceKey : null, mockCM)
            .AddRedisLeaderElection(
                builder => builder.WithSettings(settings).WithRegisteredCache(),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>().Get(serviceKey);
        options.ConnectionMultiplexerFactory.Should().NotBeNull();
        var actualCM = await options.ConnectionMultiplexerFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualCM.Should().BeSameAs(mockCM);
    }

    [Fact]
    public async Task ShouldNotSetDefaultConnectionMultiplexerFactoryWhenHostIsSpecified()
    {
        // Arrange
        settings.Host.Should().NotBeNullOrWhiteSpace();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddRedisLeaderElection(builder => builder.WithSettings(settings))
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptions<RedisSettings>>().Value;
        options.ConnectionMultiplexerFactory.Should().BeNull();
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
        var actualCM = await options.ConnectionMultiplexerFactory(
            settings,
            TestContext.Current.CancellationToken
        );
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
                builder =>
                    builder.WithConnectionMultiplexerFactory((_, _, _) => Task.FromResult(mockCM)),
                serviceKey
            )
            .BuildServiceProvider();
#pragma warning restore CA2025 // Do not pass 'IDisposable' instances into unawaited tasks

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>().Get(serviceKey);
        options.ConnectionMultiplexerFactory.Should().NotBeNull();
        var actualCM = await options.ConnectionMultiplexerFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualCM.Should().BeSameAs(mockCM);

        var leaderElection = sp.GetRequiredKeyedService<RedisLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }
}
