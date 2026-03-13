using Azure.Storage.Blobs;
using FluentAssertions;
using LeaderElection.BlobStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LeaderElection.Tests;

[Collection("Azurite Container")]
[Trait("Kind", "Integration")]
[Trait("Category", "BlobStorage")]
public sealed class BlobStorageLeaderElectionTests(AzuriteContainerFixture azuriteFixture) : TestBase
{
    private BlobServiceClient _blobServiceClient = azuriteFixture.BlobServiceClient;

    private BlobStorageSettings CreateSettings(
        string containerName, // should be unique per test to avoid conflicts
        string blobName = "leader-election-lock",
        string instanceId = "test-instance-1",
        TimeSpan? leaseDuration = null,
        TimeSpan? renewInterval = null,
        TimeSpan? retryInterval = null,
        int maxRetryAttempts = 3,
        bool enableGracefulShutdown = true,
        bool createContainerIfNotExists = true,
        string? connectionString = null
    ) =>
        new()
        {
            ConnectionString = connectionString ?? azuriteFixture.ConnectionString,
            ContainerName = containerName,
            BlobName = blobName,
            InstanceId = instanceId,
            LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(15),
            RenewInterval = renewInterval ?? TimeSpan.FromSeconds(5),
            RetryInterval = retryInterval ?? TimeSpan.FromSeconds(2),
            MaxRetryAttempts = maxRetryAttempts,
            EnableGracefulShutdown = enableGracefulShutdown,
            CreateContainerIfNotExists = createContainerIfNotExists
        };

    private BlobStorageLeaderElection CreateSUT(BlobStorageSettings options) =>
        new(
            _blobServiceClient,
            Options.Create(options),
            NullLoggerFactory.Instance.CreateLogger<BlobStorageLeaderElection>()
        );

    [Fact]
    public async Task Should_Acquire_Leadership_When_No_Other_Instance_Exists()
    {
        // Arrange
        var options = CreateSettings("test-leader-election");

        await using var leaderElection = CreateSUT(options);

        // Act
        await leaderElection.StartAsync(CancellationToken);

        // Assert
        await WaitForLeadershipChange(leaderElection, true, options.LeaseDuration);
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Not_Acquire_Leadership_When_Another_Instance_Has_Leadership()
    {
        // Arrange
        var options1 = CreateSettings(
            "test-leader-election-conflict",
            instanceId: "test-instance-1",
            leaseDuration: TimeSpan.FromSeconds(30),
            renewInterval: TimeSpan.FromSeconds(5)
        );

        var options2 = CreateSettings(
            "test-leader-election-conflict",
            instanceId: "test-instance-2",
            leaseDuration: TimeSpan.FromSeconds(30),
            renewInterval: TimeSpan.FromSeconds(5)
        );

        await using var leaderElection1 = CreateSUT(options1);
        await using var leaderElection2 = CreateSUT(options2);

        // Act
        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(15));

        await leaderElection2.StartAsync(CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken); // Give time for second instance to try

        // Assert
        leaderElection1.IsLeader.Should().BeTrue();
        leaderElection2.IsLeader.Should().BeFalse();

        await leaderElection1.StopAsync(CancellationToken);
        await leaderElection2.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Transfer_Leadership_When_Current_Leader_Stops()
    {
        // Arrange
        var options1 = CreateSettings(
            "test-leader-election-transfer",
            instanceId: "test-instance-1",
            leaseDuration: TimeSpan.FromSeconds(15),
            renewInterval: TimeSpan.FromSeconds(2),
            retryInterval: TimeSpan.FromSeconds(1)
        );

        var options2 = CreateSettings(
            "test-leader-election-transfer",
            instanceId: "test-instance-2",
            leaseDuration: TimeSpan.FromSeconds(15),
            renewInterval: TimeSpan.FromSeconds(2),
            retryInterval: TimeSpan.FromSeconds(1)
        );

        await using var leaderElection1 = CreateSUT(options1);

        await using var leaderElection2 = CreateSUT(options2);

        // Act
        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(15));

        await leaderElection2.StartAsync(CancellationToken);

        // Stop the first leader
        await leaderElection1.StopAsync(CancellationToken);

        // Assert
        await WaitForLeadershipChange(leaderElection2, true, TimeSpan.FromSeconds(20));
        leaderElection1.IsLeader.Should().BeFalse();
        leaderElection2.IsLeader.Should().BeTrue();

        await leaderElection2.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Run_Task_Only_When_Leader()
    {
        // Arrange
        var options = CreateSettings("test-leader-election-task");

        await using var leaderElection = CreateSUT(options);

        var taskExecuted = false;

        // Act
        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true, options.LeaseDuration);

        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationToken);

        // Assert
        taskExecuted.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Not_Run_Task_When_Not_Leader()
    {
        // Arrange
        var options = CreateSettings("test-leader-election-no-task");

        await using var leaderElection = CreateSUT(options);

        var taskExecuted = false;

        // Act - Don't start the leader election, so it won't be leader
        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationToken);

        // Assert
        taskExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Handle_Manual_Leadership_Acquisition()
    {
        // Arrange
        var options = CreateSettings("test-leader-election-manual");

        await using var leaderElection = CreateSUT(options);

        // Act
        var result = await leaderElection.TryAcquireLeadershipAsync(CancellationToken);

        // Assert
        result.Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Create_Container_And_Blob_If_Not_Exists()
    {
        // Arrange
        var options = CreateSettings(
            $"test-container-{Guid.NewGuid():N}",
            createContainerIfNotExists: true);

        await using var leaderElection = CreateSUT(options);

        // Act
        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true, options.LeaseDuration);

        // Assert
        leaderElection.IsLeader.Should().BeTrue();

        // Verify container and blob were created
        var containerClient = _blobServiceClient.GetBlobContainerClient(options.ContainerName);
        var containerExists = await containerClient.ExistsAsync(CancellationToken);
        containerExists.Value.Should().BeTrue();

        var blobClient = containerClient.GetBlobClient(options.BlobName);
        var blobExists = await blobClient.ExistsAsync(CancellationToken);
        blobExists.Value.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }
}