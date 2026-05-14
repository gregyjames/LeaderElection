using LeaderElection.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minio;
using Moq;

namespace LeaderElection.Tests;

public sealed class S3ServiceBuilderExtensionsTests
{
    // Settings with all properties set to non-default values.
    private readonly S3Settings settings;
    private readonly IMinioClient mockMinioClient;

    public S3ServiceBuilderExtensionsTests()
    {
        mockMinioClient = Mock.Of<IMinioClient>();
        settings = new()
        {
            BucketName = "test-bucket",
            ObjectKey = "test-object",
            MinioClientFactory = _ => mockMinioClient,
            InstanceId = "foo",
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            RetryBackoffFactor = 1.5,
            RetryJitter = 0.15,
            MaxRetryInterval = TimeSpan.FromSeconds(100),
            EnableGracefulShutdown = false,
        };
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddOptionsCorrectlyWhenUsingDirectSettings(string? serviceKey)
    {
        // Arrange

        // Act
        await using var sp = new ServiceCollection()
            .AddS3LeaderElection(builder: b => b.WithSettings(settings), serviceKey: serviceKey)
            .BuildServiceProvider();

        // Assert
        var actualSettings = sp.GetRequiredService<IOptionsMonitor<S3Settings>>().Get(serviceKey);
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
                    ["LeaderElection:S3:BucketName"] = settings.BucketName,
                    ["LeaderElection:S3:ObjectKey"] = settings.ObjectKey,
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
            .AddS3LeaderElection(
                builder: b =>
                    b.WithMinioClientFactory(settings.MinioClientFactory!)
                        .WithConfiguration("LeaderElection:S3")
                        .WithConfiguration(config.GetSection("LeaderElection:Base"))
                        .WithInstanceId(settings.InstanceId)
                        .WithSettings(s =>
                            s.EnableGracefulShutdown = settings.EnableGracefulShutdown
                        ),
                serviceKey: serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var actualSettings = sp.GetRequiredService<IOptionsMonitor<S3Settings>>().Get(serviceKey);
        actualSettings.Should().BeEquivalentTo(settings);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("foo", false)]
    [InlineData(null, false)]
    public async Task ShouldAddServicesCorrectlyWhenUsingDefaultClient(
        string? serviceKey,
        bool useKeyedMC
    )
    {
        // Arrange
        var customMC = Mock.Of<IMinioClient>();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton(useKeyedMC ? serviceKey : null, customMC)
            .AddS3LeaderElection(
                builder => builder.WithSettings(settings).WithRegisteredMinioClient(),
                serviceKey: serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<S3Settings>>();
        var mcFactory = options.Get(serviceKey).MinioClientFactory;
        mcFactory.Should().NotBeNull();
        var actualMC = mcFactory.Invoke(settings);
        actualMC.Should().BeSameAs(customMC);

        var leaderElection = sp.GetRequiredKeyedService<S3LeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingMCInstance(string? serviceKey)
    {
        // Arrange
        var customMC = Mock.Of<IMinioClient>();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddS3LeaderElection(
                builder => builder.WithSettings(settings).WithMinioClient(customMC),
                serviceKey: serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<S3Settings>>();
        var mcFactory = options.Get(serviceKey).MinioClientFactory;
        mcFactory.Should().NotBeNull();
        var actualMC = mcFactory.Invoke(settings);
        actualMC.Should().BeSameAs(customMC);

        var leaderElection = sp.GetRequiredKeyedService<S3LeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }
}
