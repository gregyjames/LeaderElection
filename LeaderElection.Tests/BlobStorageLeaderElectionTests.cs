using Azure.Storage.Blobs;
using FluentAssertions;
using LeaderElection.BlobStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.Azurite;
using Xunit;

namespace LeaderElection.Tests;

public class BlobStorageLeaderElectionTests : TestBase, IAsyncDisposable
{
    private readonly AzuriteContainer _azuriteContainer;
    private readonly BlobServiceClient _blobServiceClient;

    public BlobStorageLeaderElectionTests()
    {
        _azuriteContainer = new AzuriteBuilder()
            .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
            .Build();
        
        _azuriteContainer.StartAsync().Wait();
        
        var connectionString = _azuriteContainer.GetConnectionString();
        _blobServiceClient = new BlobServiceClient(connectionString);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _azuriteContainer.DisposeAsync();
    }

    [Fact]
    public async Task Should_Acquire_Leadership_When_No_Other_Instance_Exists()
    {
        // Arrange
        var options = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = "test-leader-election",
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-1",
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromSeconds(2),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var leaderElection = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        // Act
        await leaderElection.StartAsync(CancellationTokenSource.Token);
        
        // Assert
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(15));
        leaderElection.IsLeader.Should().BeTrue();
        
        await leaderElection.StopAsync(CancellationTokenSource.Token);
    }

    [Fact]
    public async Task Should_Not_Acquire_Leadership_When_Another_Instance_Has_Leadership()
    {
        // Arrange
        var options1 = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = "test-leader-election-conflict",
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-1",
            LeaseDuration = TimeSpan.FromSeconds(30),
            RenewInterval = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromSeconds(2),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var options2 = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = "test-leader-election-conflict",
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-2",
            LeaseDuration = TimeSpan.FromSeconds(30),
            RenewInterval = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromSeconds(2),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var leaderElection1 = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options1),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        var leaderElection2 = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options2),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        // Act
        await leaderElection1.StartAsync(CancellationTokenSource.Token);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(15));
        
        await leaderElection2.StartAsync(CancellationTokenSource.Token);
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationTokenSource.Token); // Give time for second instance to try

        // Assert
        leaderElection1.IsLeader.Should().BeTrue();
        leaderElection2.IsLeader.Should().BeFalse();
        
        await leaderElection1.StopAsync(CancellationTokenSource.Token);
        await leaderElection2.StopAsync(CancellationTokenSource.Token);
    }

    [Fact]
    public async Task Should_Transfer_Leadership_When_Current_Leader_Stops()
    {
        // Arrange
        var options1 = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = "test-leader-election-transfer",
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-1",
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var options2 = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = "test-leader-election-transfer",
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-2",
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var leaderElection1 = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options1),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        var leaderElection2 = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options2),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        // Act
        await leaderElection1.StartAsync(CancellationTokenSource.Token);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(15));
        
        await leaderElection2.StartAsync(CancellationTokenSource.Token);
        
        // Stop the first leader
        await leaderElection1.StopAsync(CancellationTokenSource.Token);
        
        // Assert
        await WaitForLeadershipChange(leaderElection2, true, TimeSpan.FromSeconds(20));
        leaderElection1.IsLeader.Should().BeFalse();
        leaderElection2.IsLeader.Should().BeTrue();
        
        await leaderElection2.StopAsync(CancellationTokenSource.Token);
    }

    [Fact]
    public async Task Should_Run_Task_Only_When_Leader()
    {
        // Arrange
        var options = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = "test-leader-election-task",
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-1",
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromSeconds(2),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var leaderElection = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        var taskExecuted = false;

        // Act
        await leaderElection.StartAsync(CancellationTokenSource.Token);
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(15));
        
        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationTokenSource.Token);
        
        // Assert
        taskExecuted.Should().BeTrue();
        
        await leaderElection.StopAsync(CancellationTokenSource.Token);
    }

    [Fact]
    public async Task Should_Not_Run_Task_When_Not_Leader()
    {
        // Arrange
        var options = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = "test-leader-election-no-task",
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-1",
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromSeconds(2),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var leaderElection = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        var taskExecuted = false;

        // Act - Don't start the leader election, so it won't be leader
        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationTokenSource.Token);
        
        // Assert
        taskExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Handle_Manual_Leadership_Acquisition()
    {
        // Arrange
        var options = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = "test-leader-election-manual",
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-1",
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromSeconds(2),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var leaderElection = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        // Act
        var result = await leaderElection.TryAcquireLeadershipAsync(CancellationTokenSource.Token);
        
        // Assert
        result.Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();
        
        await leaderElection.StopAsync(CancellationTokenSource.Token);
    }

    [Fact]
    public async Task Should_Create_Container_And_Blob_If_Not_Exists()
    {
        // Arrange
        var containerName = $"test-container-{Guid.NewGuid():N}";
        var options = new BlobStorageSettings
        {
            ConnectionString = _azuriteContainer.GetConnectionString(),
            ContainerName = containerName,
            BlobName = "leader-election-lock",
            InstanceId = "test-instance-1",
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewInterval = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromSeconds(2),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true,
            CreateContainerIfNotExists = true
        };

        var leaderElection = new BlobStorageLeaderElection(
            _blobServiceClient,
            Options.Create(options),
            LoggerFactory.CreateLogger<BlobStorageLeaderElection>());

        // Act
        await leaderElection.StartAsync(CancellationTokenSource.Token);
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(15));
        
        // Assert
        leaderElection.IsLeader.Should().BeTrue();
        
        // Verify container and blob were created
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var containerExists = await containerClient.ExistsAsync();
        containerExists.Value.Should().BeTrue();
        
        var blobClient = containerClient.GetBlobClient("leader-election-lock");
        var blobExists = await blobClient.ExistsAsync();
        blobExists.Value.Should().BeTrue();
        
        await leaderElection.StopAsync(CancellationTokenSource.Token);
    }
} 