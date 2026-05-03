using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using LeaderElection.BlobStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace LeaderElection.Tests;

public sealed class BlobStorageServiceBuilderExtensionsTests
{
    // Settings with all properties set to non-default values.
    private readonly BlobStorageSettings settings = new()
    {
        ConnectionString = "...",
        ContainerName = "container",
        BlobName = "blob",
        LeaseDuration = TimeSpan.FromSeconds(20),
        CreateContainerIfNotExists = false,
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
            .AddBlobStorageLeaderElection(b => b.WithSettings(settings), serviceKey)
            .BuildServiceProvider();

        // Assert
        var actualSettings = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>()
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
                    ["LeaderElection:BlobStorage:ConnectionString"] = settings.ConnectionString,
                    ["LeaderElection:BlobStorage:ContainerName"] = settings.ContainerName,
                    ["LeaderElection:BlobStorage:BlobName"] = settings.BlobName,
                    ["LeaderElection:BlobStorage:LeaseDuration"] =
                        settings.LeaseDuration.ToString(),
                    ["LeaderElection:BlobStorage:CreateContainerIfNotExists"] =
                        settings.CreateContainerIfNotExists.ToString(),
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
            .AddBlobStorageLeaderElection(
                builder =>
                    builder
                        .WithConfiguration("LeaderElection:BlobStorage")
                        .WithConfiguration(config.GetSection("LeaderElection:Base"))
                        .WithInstanceId(settings.InstanceId)
                        .WithSettings(s =>
                            s.EnableGracefulShutdown = settings.EnableGracefulShutdown
                        ),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var actualSettings = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>()
            .Get(serviceKey);
        actualSettings.Should().BeEquivalentTo(settings);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("foo", false)]
    [InlineData(null, false)]
    public async Task ShouldSetDefaultBlobClientFactoryWhenConnectionStringIsNull(
        string? serviceKey,
        bool useKeyedBSC
    )
    {
        // Arrange
        var settings = new BlobStorageSettings
        {
            ConnectionString = null, // use BSC from DI
            CreateContainerIfNotExists = false,
        };

        var mockBC = Mock.Of<BlobClient>();
        var mockBCC = Mock.Of<BlobContainerClient>(m =>
            m.GetBlobClient(settings.BlobName) == mockBC
        );
        var mockBSC = Mock.Of<BlobServiceClient>(m =>
            m.GetBlobContainerClient(settings.ContainerName) == mockBCC
        );
        var bsc = new BlobServiceClient("UseDevelopmentStorage=true");

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton(useKeyedBSC ? serviceKey : null, bsc)
            .AddBlobStorageLeaderElection(builder => builder.WithSettings(settings), serviceKey)
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>().Get(serviceKey);
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC.Should().NotBeNull();
        actualBC.Name.Should().Be(settings.BlobName);
        actualBC.BlobContainerName.Should().Be(settings.ContainerName);
        actualBC
            .GetParentBlobContainerClient()
            .GetParentBlobServiceClient()
            .Uri.Should()
            .Be(bsc.Uri);

        var leaderElection = sp.GetRequiredKeyedService<BlobStorageLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("foo", false)]
    [InlineData(null, false)]
    public async Task ShouldUseRegisteredBlobClientFactoryWhenTold(
        string? serviceKey,
        bool useKeyedBSC
    )
    {
        // Arrange
        var settings = new BlobStorageSettings
        {
            ConnectionString = "invalid; will be ignored",
            CreateContainerIfNotExists = false,
        };

        var bsc = new BlobServiceClient("UseDevelopmentStorage=true");

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddKeyedSingleton(useKeyedBSC ? serviceKey : null, bsc)
            .AddBlobStorageLeaderElection(
                builder => builder.WithSettings(settings).WithRegisteredBlobServiceClient(),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>().Get(serviceKey);
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC.Should().NotBeNull();
        actualBC.Name.Should().Be(settings.BlobName);
        actualBC.BlobContainerName.Should().Be(settings.ContainerName);
        actualBC
            .GetParentBlobContainerClient()
            .GetParentBlobServiceClient()
            .Uri.Should()
            .Be(bsc.Uri);
    }

    [Fact]
    public async Task ShouldNotSetDefaultBlobClientFactoryWhenConnectionStringIsUsed()
    {
        // Arrange
        var settings = new BlobStorageSettings()
        {
            ConnectionString = "UseDevelopmentStorage=true",
            CreateContainerIfNotExists = false,
        };

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(b => b.WithSettings(settings))
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptions<BlobStorageSettings>>().Value;
        options.BlobClientFactory.Should().BeNull();
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingBCInstance(string? serviceKey)
    {
        // Arrange
        var mockBC = Mock.Of<BlobClient>();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(b => b.WithBlobClient(mockBC), serviceKey)
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>().Get(serviceKey);
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC.Should().BeSameAs(mockBC);

        var leaderElection = sp.GetRequiredKeyedService<BlobStorageLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingBSCInstance(string? serviceKey)
    {
        // Arrange
        var bsc = new BlobServiceClient("UseDevelopmentStorage=true");

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(b => b.WithBlobServiceClient(bsc), serviceKey)
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>().Get(serviceKey);
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC
            .GetParentBlobContainerClient()
            .GetParentBlobServiceClient()
            .Uri.Should()
            .Be(bsc.Uri);

        var leaderElection = sp.GetRequiredKeyedService<BlobStorageLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingCustomBCFactory(string? serviceKey)
    {
        // Arrange
        var mockBC = Mock.Of<BlobClient>();

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(
                b => b.WithBlobClientFactory((_, _, _) => Task.FromResult(mockBC)),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>().Get(serviceKey);
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC.Should().BeSameAs(mockBC);

        var leaderElection = sp.GetRequiredKeyedService<BlobStorageLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingCustomBSC(string? serviceKey)
    {
        // Arrange
        var bsc = new BlobServiceClient("UseDevelopmentStorage=true");

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(
                builder => builder.WithSettings(settings).WithBlobServiceClientFactory(_ => bsc),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>().Get(serviceKey);
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC
            .GetParentBlobContainerClient()
            .GetParentBlobServiceClient()
            .Uri.Should()
            .Be(bsc.Uri);

        var leaderElection = sp.GetRequiredKeyedService<BlobStorageLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().BeSameAs(leaderElection);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingBCC(string? serviceKey)
    {
        // Arrange
        var bsc = new BlobServiceClient("UseDevelopmentStorage=true");
        var bcc = bsc.GetBlobContainerClient(settings.ContainerName);

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(
                builder => builder.WithSettings(settings).WithContainerClient(bcc),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>().Get(serviceKey);
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC.GetParentBlobContainerClient().Uri.Should().Be(bcc.Uri);

        var leaderElection = sp.GetRequiredKeyedService<BlobStorageLeaderElection>(serviceKey);
        sp.GetRequiredKeyedService<ILeaderElection>(serviceKey).Should().Be(leaderElection);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData(null)]
    public async Task ShouldAddServicesCorrectlyWhenUsingCustomBCC(string? serviceKey)
    {
        // Arrange
        settings.CreateContainerIfNotExists = true; // for coverage

        var mockBC = Mock.Of<BlobClient>();
        var mockBCC = Mock.Of<BlobContainerClient>(m =>
            m.GetBlobClient(settings.BlobName) == mockBC
            && m.CreateIfNotExistsAsync(
                publicAccessType: default,
                metadata: default,
                encryptionScopeOptions: default,
                cancellationToken: It.IsAny<CancellationToken>()
            )
                == Task.FromResult(
                    Azure.Response.FromValue(
                        BlobsModelFactory.BlobContainerInfo(new("etag"), DateTimeOffset.UtcNow),
                        Mock.Of<Azure.Response>(m => m.Status == 201)
                    )
                )
        );

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(
                builder => builder.WithSettings(settings).WithContainerClient(mockBCC),
                serviceKey
            )
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptionsMonitor<BlobStorageSettings>>().Get(serviceKey);
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC.Should().BeSameAs(mockBC);

        Mock.Get(mockBCC).VerifyAll();
    }

    [Fact]
    public async Task ShouldAddServicesCorrectlyWhenUsingEndpointOverload()
    {
        // Arrange
        var endpoint = "https://foo.fake"; // will not actually connect to Azure in this test

        // Act
        await using var sp = new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(endpoint: endpoint, settings)
            .BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<IOptions<BlobStorageSettings>>().Value;
        options.BlobClientFactory.Should().NotBeNull();
        var actualBC = await options.BlobClientFactory(
            settings,
            TestContext.Current.CancellationToken
        );
        actualBC.Should().NotBeNull();
        actualBC
            .Uri.ToString()
            .Should()
            .Be($"{endpoint}/{settings.ContainerName}/{settings.BlobName}");
    }
}
