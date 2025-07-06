using FluentAssertions;
using LeaderElection.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace LeaderElection.Tests;

public class RedisLeaderElectionTests : TestBase, IAsyncDisposable
{
    private readonly RedisContainer _redisContainer;
    private readonly IConnectionMultiplexer _connectionMultiplexer;

    public RedisLeaderElectionTests()
    {
        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();
        
        _redisContainer.StartAsync().Wait();
        
        var connectionString = _redisContainer.GetConnectionString();
        _connectionMultiplexer = ConnectionMultiplexer.Connect(connectionString);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _connectionMultiplexer?.Dispose();
        await _redisContainer.DisposeAsync();
    }

    [Fact]
    public async Task Should_Acquire_Leadership_When_No_Other_Instance_Exists()
    {
        // Arrange
        var options = new RedisSettings
        {
            LockKey = "test-leader-election",
            InstanceId = "test-instance-1",
            LockExpiry = TimeSpan.FromSeconds(10),
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true
        };

        var leaderElection = new RedisLeaderElection(
            _connectionMultiplexer,
            Options.Create(options),
            LoggerFactory.CreateLogger<RedisLeaderElection>());

        // Act
        await leaderElection.StartAsync(CancellationTokenSource.Token);
        
        // Assert
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(10));
        leaderElection.IsLeader.Should().BeTrue();
        
        await leaderElection.StopAsync(CancellationTokenSource.Token);
    }

    [Fact]
    public async Task Should_Not_Acquire_Leadership_When_Another_Instance_Has_Leadership()
    {
        // Arrange
        var options1 = new RedisSettings
        {
            LockKey = "test-leader-election-conflict",
            InstanceId = "test-instance-1",
            LockExpiry = TimeSpan.FromSeconds(30),
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true
        };

        var options2 = new RedisSettings
        {
            LockKey = "test-leader-election-conflict",
            InstanceId = "test-instance-2",
            LockExpiry = TimeSpan.FromSeconds(30),
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true
        };

        var leaderElection1 = new RedisLeaderElection(
            _connectionMultiplexer,
            Options.Create(options1),
            LoggerFactory.CreateLogger<RedisLeaderElection>());

        var leaderElection2 = new RedisLeaderElection(
            _connectionMultiplexer,
            Options.Create(options2),
            LoggerFactory.CreateLogger<RedisLeaderElection>());

        // Act
        await leaderElection1.StartAsync(CancellationTokenSource.Token);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(10));
        
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
        var options1 = new RedisSettings
        {
            LockKey = "test-leader-election-transfer",
            InstanceId = "test-instance-1",
            LockExpiry = TimeSpan.FromSeconds(5),
            RenewInterval = TimeSpan.FromSeconds(1),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true
        };

        var options2 = new RedisSettings
        {
            LockKey = "test-leader-election-transfer",
            InstanceId = "test-instance-2",
            LockExpiry = TimeSpan.FromSeconds(5),
            RenewInterval = TimeSpan.FromSeconds(1),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true
        };

        var leaderElection1 = new RedisLeaderElection(
            _connectionMultiplexer,
            Options.Create(options1),
            LoggerFactory.CreateLogger<RedisLeaderElection>());

        var leaderElection2 = new RedisLeaderElection(
            _connectionMultiplexer,
            Options.Create(options2),
            LoggerFactory.CreateLogger<RedisLeaderElection>());

        // Act
        await leaderElection1.StartAsync(CancellationTokenSource.Token);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(10));
        
        await leaderElection2.StartAsync(CancellationTokenSource.Token);
        
        // Stop the first leader
        await leaderElection1.StopAsync(CancellationTokenSource.Token);
        
        // Assert
        await WaitForLeadershipChange(leaderElection2, true, TimeSpan.FromSeconds(15));
        leaderElection1.IsLeader.Should().BeFalse();
        leaderElection2.IsLeader.Should().BeTrue();
        
        await leaderElection2.StopAsync(CancellationTokenSource.Token);
    }

    [Fact]
    public async Task Should_Run_Task_Only_When_Leader()
    {
        // Arrange
        var options = new RedisSettings
        {
            LockKey = "test-leader-election-task",
            InstanceId = "test-instance-1",
            LockExpiry = TimeSpan.FromSeconds(10),
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true
        };

        var leaderElection = new RedisLeaderElection(
            _connectionMultiplexer,
            Options.Create(options),
            LoggerFactory.CreateLogger<RedisLeaderElection>());

        var taskExecuted = false;

        // Act
        await leaderElection.StartAsync(CancellationTokenSource.Token);
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(10));
        
        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationTokenSource.Token);
        
        // Assert
        taskExecuted.Should().BeTrue();
        
        await leaderElection.StopAsync(CancellationTokenSource.Token);
    }

    [Fact]
    public async Task Should_Not_Run_Task_When_Not_Leader()
    {
        // Arrange
        var options = new RedisSettings
        {
            LockKey = "test-leader-election-no-task",
            InstanceId = "test-instance-1",
            LockExpiry = TimeSpan.FromSeconds(10),
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true
        };

        var leaderElection = new RedisLeaderElection(
            _connectionMultiplexer,
            Options.Create(options),
            LoggerFactory.CreateLogger<RedisLeaderElection>());

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
        var options = new RedisSettings
        {
            LockKey = "test-leader-election-manual",
            InstanceId = "test-instance-1",
            LockExpiry = TimeSpan.FromSeconds(10),
            RenewInterval = TimeSpan.FromSeconds(2),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryAttempts = 3,
            EnableGracefulShutdown = true
        };

        var leaderElection = new RedisLeaderElection(
            _connectionMultiplexer,
            Options.Create(options),
            LoggerFactory.CreateLogger<RedisLeaderElection>());

        // Act
        var result = await leaderElection.TryAcquireLeadershipAsync(CancellationTokenSource.Token);
        
        // Assert
        result.Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();
        
        await leaderElection.StopAsync(CancellationTokenSource.Token);
    }
} 