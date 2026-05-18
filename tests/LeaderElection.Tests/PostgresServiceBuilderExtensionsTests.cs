using LeaderElection.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LeaderElection.Tests;

public class PostgresServiceBuilderExtensionsTests
{
    // Settings with all properties set to non-default values.
    private readonly PostgresSettings settings = new()
    {
        ConnectionString = "Host=localhost",
        LockId = 12345,
        InstanceId = "foo",
        RenewInterval = TimeSpan.FromSeconds(2),
        RetryInterval = TimeSpan.FromSeconds(1),
        MaxRetryAttempts = 300,
        EnableGracefulShutdown = false,
    };

    [Fact]
    public async Task ShouldRegisterAllRequiredServices()
    {
        // Arrange & Act
        await using var serviceProvider = new ServiceCollection()
            .AddLogging()
            .AddPostgresLeaderElection(settings)
            .BuildServiceProvider();

        // Assert
        serviceProvider.GetService<ILeaderElection>().Should().NotBeNull();
        serviceProvider.GetService<PostgresLeaderElection>().Should().NotBeNull();
        serviceProvider.GetService<IOptions<PostgresSettings>>().Should().NotBeNull();
        var validateOptionsEnumerable = serviceProvider
            .GetServices<IValidateOptions<PostgresSettings>>()
            .ToList();
        validateOptionsEnumerable.Should().NotBeEmpty();
        validateOptionsEnumerable.Should().ContainSingle(v => v is PostgresSettingsValidator);
    }

    [Fact]
    public async Task ShouldFailValidationWhenSettingsAreInvalid()
    {
        // Arrange
        await using var serviceProvider = new ServiceCollection()
            .AddPostgresLeaderElection(options =>
            {
                // Specifically leave InstanceId empty
                options.InstanceId = string.Empty;
                options.LockId = 12345;
            })
            .BuildServiceProvider();

        var optionsProvider = serviceProvider.GetRequiredService<IOptions<PostgresSettings>>();

        // Act & Assert
        var ex = Assert.Throws<OptionsValidationException>(() => optionsProvider.Value);
        ex.Failures.Should().Contain(f => f.Contains("InstanceId"));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddOptionsCorrectlyWhenUsingDirectSettings(string? serviceKey)
    {
        // Arrange

        // Act
        await using var sp = new ServiceCollection()
            .AddPostgresLeaderElection(b => b.WithSettings(settings), serviceKey: serviceKey)
            .BuildServiceProvider();

        // Assert
        var actualSettings = sp.GetRequiredService<IOptionsMonitor<PostgresSettings>>()
            .Get(serviceKey);
        actualSettings.DataSourceFactory.Should().NotBeNull();
        actualSettings.DataSourceFactory = null; // ignore factory for equivalence check
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
                    ["LeaderElection:Postgres:ConnectionString"] = settings.ConnectionString,
                    ["LeaderElection:Postgres:LockId"] = settings.LockId.ToString(),
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
            .AddPostgresLeaderElection(
                builder =>
                    builder
                        .WithConfiguration("LeaderElection:Postgres")
                        .WithConfiguration(config.GetSection("LeaderElection:Base"))
                        .WithInstanceId(settings.InstanceId)
                        .WithSettings(s =>
                            s.EnableGracefulShutdown = settings.EnableGracefulShutdown
                        ),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var actualSettings = sp.GetRequiredService<IOptionsMonitor<PostgresSettings>>()
            .Get(serviceKey);
        actualSettings.ConnectionString.Should().Be(settings.ConnectionString);
        actualSettings.LockId.Should().Be(settings.LockId);
        actualSettings.InstanceId.Should().Be(settings.InstanceId);
        actualSettings.RenewInterval.Should().Be(settings.RenewInterval);
        actualSettings.RetryInterval.Should().Be(settings.RetryInterval);
        actualSettings.MaxRetryAttempts.Should().Be(settings.MaxRetryAttempts);
        actualSettings.EnableGracefulShutdown.Should().Be(settings.EnableGracefulShutdown);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("foo", false)]
    [InlineData(null, false)]
    public async Task ShouldSetRegisteredDataSourceFactory(string? serviceKey, bool useKeyedDS)
    {
        // Arrange
        var defaultSettings = new PostgresSettings();

        var dummyDS = new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton(useKeyedDS ? serviceKey : null, dummyDS)
            .AddPostgresLeaderElection(builder => builder.WithSettings(defaultSettings), serviceKey)
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<PostgresSettings>>().Get(serviceKey);
        options.DataSourceFactory.Should().NotBeNull();
        var actualDS = options.DataSourceFactory(defaultSettings);
        actualDS.Should().BeSameAs(dummyDS);

        var leaderElection = sp.GetRequiredKeyedService<PostgresLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Fact]
    public async Task ShouldUseDefaultRegisteredDataSource()
    {
        // Arrange
        var dummyDS = new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton<NpgsqlDataSource>(null, dummyDS)
            .AddPostgresLeaderElection(builder => builder.WithRegisteredDataSource())
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptions<PostgresSettings>>().Value;
        options.DataSourceFactory.Should().NotBeNull();
        var actualDS = options.DataSourceFactory(new());
        actualDS.Should().BeSameAs(dummyDS);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldUseRegisteredDataSource(string? dsServiceKey)
    {
        // Arrange
        var dummyDS = new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton(dsServiceKey, dummyDS)
            .AddPostgresLeaderElection(builder => builder.WithRegisteredDataSource(dsServiceKey))
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptions<PostgresSettings>>().Value;
        options.DataSourceFactory.Should().NotBeNull();
        var actualDS = options.DataSourceFactory(new());
        actualDS.Should().BeSameAs(dummyDS);
    }
}
