using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using LeaderElection.BlobStorage;
using Microsoft.Extensions.DependencyInjection;

namespace LeaderElection.Tests;

[Collection("Azurite Container")]
[Trait("Kind", "Integration")]
[Trait("Category", "BlobStorage")]
public sealed class BlobStorageLeaderElectionTests(AzuriteContainerFixture azuriteFixture)
    : TestBase
{
    private readonly BlobServiceClient _blobServiceClient = azuriteFixture.BlobServiceClient;

    private BlobStorageSettings CreateSettings(
        string containerName, // should be unique per test to avoid conflicts
        string blobName = "leader-election-lock",
        string instanceId = "test-instance-1",
        TimeSpan? leaseDuration = null,
        TimeSpan? renewInterval = null,
        TimeSpan? retryInterval = null,
        TimeSpan? maxRetryInterval = null,
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
            MaxRetryInterval = maxRetryInterval ?? TimeSpan.FromSeconds(60),
            EnableGracefulShutdown = enableGracefulShutdown,
            CreateContainerIfNotExists = createContainerIfNotExists,
        };

    private BlobStorageLeaderElection CreateSut(BlobStorageSettings settings) =>
        new ServiceCollection()
            .AddLogging()
            .AddBlobStorageLeaderElection(builder =>
                builder.WithSettings(settings).WithBlobServiceClient(_blobServiceClient)
            )
            .BuildServiceProvider()
            .GetRequiredService<BlobStorageLeaderElection>();

    [Fact]
    public async Task ShouldAcquireLeadershipWhenNoOtherInstanceExists()
    {
        // Arrange
        var options = CreateSettings("test-leader-election");

        await using var leaderElection = CreateSut(options);

        // Act
        await leaderElection.StartAsync(CancellationToken);

        // Assert
        await WaitForLeadershipChange(leaderElection, true, options.LeaseDuration);
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldNotAcquireLeadershipWhenAnotherInstanceHasLeadership()
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

        await using var leaderElection1 = CreateSut(options1);
        await using var leaderElection2 = CreateSut(options2);

        // Act
        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(15));

        await leaderElection2.StartAsync(CancellationToken);
        await TimeProvider.Delay(TimeSpan.FromSeconds(5), CancellationToken); // Give time for second instance to try

        // Assert
        leaderElection1.IsLeader.Should().BeTrue();
        leaderElection2.IsLeader.Should().BeFalse();

        await leaderElection1.StopAsync(CancellationToken);
        await leaderElection2.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldTransferLeadershipWhenCurrentLeaderStops()
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

        await using var leaderElection1 = CreateSut(options1);

        await using var leaderElection2 = CreateSut(options2);

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
    public async Task ShouldRunTaskOnlyWhenLeader()
    {
        // Arrange
        var options = CreateSettings("test-leader-election-task");

        await using var leaderElection = CreateSut(options);

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
    public async Task ShouldNotRunTaskWhenNotLeader()
    {
        // Arrange
        var options = CreateSettings("test-leader-election-no-task");

        await using var leaderElection = CreateSut(options);

        var taskExecuted = false;

        // Act - Don't start the leader election, so it won't be leader
        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationToken);

        // Assert
        taskExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldHandleManualLeadershipAcquisition()
    {
        // Arrange
        var options = CreateSettings("test-leader-election-manual");

        await using var leaderElection = CreateSut(options);
        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true);

        // Act
        var result = await leaderElection.TryAcquireLeadershipAsync(CancellationToken);

        // Assert
        result.Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldCreateContainerAndBlobIfNotExists()
    {
        // Arrange
        var options = CreateSettings(
            $"test-container-{Guid.NewGuid():N}",
            createContainerIfNotExists: true
        );

        await using var leaderElection = CreateSut(options);

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

    [Fact]
    public async Task ShouldHandleRenewalConflict()
    {
        var options = CreateSettings(
            "test-renewal-conflict",
            leaseDuration: TimeSpan.FromSeconds(15)
        );
        await using var leaderElection = CreateSut(options);

        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true, options.LeaseDuration);

        var containerClient = _blobServiceClient.GetBlobContainerClient(options.ContainerName);
        var blobClient = containerClient.GetBlobClient(options.BlobName);
        var leaseClient = blobClient.GetBlobLeaseClient();

        // Break the lease immediately
        await leaseClient.BreakAsync(TimeSpan.Zero, cancellationToken: CancellationToken);

        // Acquire it with a DIFFERENT lease ID (by not specifying one)
        await leaseClient.AcquireAsync(
            TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken
        );

        await WaitForLeadershipChange(leaderElection, false, TimeSpan.FromSeconds(20));
        leaderElection.IsLeader.Should().BeFalse();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldRetainLeadershipAfterAtLeastOneRenewalCycle()
    {
        // Arrange
        var options = CreateSettings(
            "test-leader-election-renewal",
            renewInterval: TimeSpan.FromSeconds(1)
        );

        await using var leaderElection = CreateSut(options);

        // Act & Assert
        await TestShouldRetainLeadershipAfterAtLeastOneRenewalCycle(leaderElection, options);
    }
}
