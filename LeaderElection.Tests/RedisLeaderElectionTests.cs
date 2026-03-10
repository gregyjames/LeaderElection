using FluentAssertions;
using LeaderElection.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LeaderElection.Tests;

[Collection("Redis Container")]
public sealed class RedisLeaderElectionTests(RedisContainerFixture redisFixture) : TestBase
{
    private static RedisSettings CreateSettings(
        string lockKey, // should be unique per test to avoid conflicts
        string instanceId = "test-instance-1",
        TimeSpan? lockExpiry = null,
        TimeSpan? renewInterval = null,
        TimeSpan? retryInterval = null,
        int maxRetryAttempts = 3,
        bool enableGracefulShutdown = true
    ) =>
        new()
        {
            LockKey = lockKey,
            InstanceId = instanceId,
            LockExpiry = lockExpiry ?? TimeSpan.FromSeconds(10),
            RenewInterval = renewInterval ?? TimeSpan.FromSeconds(2),
            RetryInterval = retryInterval ?? TimeSpan.FromSeconds(1),
            MaxRetryAttempts = maxRetryAttempts,
            EnableGracefulShutdown = enableGracefulShutdown,
        };

    private RedisLeaderElection CreateSUT(RedisSettings options) =>
        new(
            redisFixture.ConnectionMultiplexer,
            Options.Create(options),
            NullLoggerFactory.Instance.CreateLogger<RedisLeaderElection>()
        );

    [Fact]
    public async Task Should_Acquire_Leadership_When_No_Other_Instance_Exists()
    {
        // Arrange
        var options = CreateSettings("test-leader-election", lockExpiry: TimeSpan.FromSeconds(10));

        await using var leaderElection = CreateSUT(options);

        // Act
        await leaderElection.StartAsync(CancellationToken);

        // Assert
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(10));
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Not_Acquire_Leadership_When_Another_Instance_Has_Leadership()
    {
        // Arrange
        var options1 = CreateSettings(
            "test-leader-election-conflict",
            "test-instance-1",
            lockExpiry: TimeSpan.FromSeconds(30)
        );

        var options2 = CreateSettings(
            "test-leader-election-conflict",
            "test-instance-2",
            lockExpiry: TimeSpan.FromSeconds(30)
        );

        await using var leaderElection1 = CreateSUT(options1);
        await using var leaderElection2 = CreateSUT(options2);

        // Act
        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(10));

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
            "test-instance-1",
            lockExpiry: TimeSpan.FromSeconds(5),
            renewInterval: TimeSpan.FromSeconds(1)
        );

        var options2 = CreateSettings(
            "test-leader-election-transfer",
            "test-instance-2",
            lockExpiry: TimeSpan.FromSeconds(5),
            renewInterval: TimeSpan.FromSeconds(1)
        );

        await using var leaderElection1 = CreateSUT(options1);
        await using var leaderElection2 = CreateSUT(options2);

        // Act
        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(10));

        await leaderElection2.StartAsync(CancellationToken);

        // Stop the first leader
        await leaderElection1.StopAsync(CancellationToken);

        // Assert
        await WaitForLeadershipChange(leaderElection2, true, TimeSpan.FromSeconds(15));
        leaderElection1.IsLeader.Should().BeFalse();
        leaderElection2.IsLeader.Should().BeTrue();

        await leaderElection2.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Run_Task_Only_When_Leader()
    {
        // Arrange
        var options = CreateSettings(
            "test-leader-election-task",
            lockExpiry: TimeSpan.FromSeconds(10)
        );

        await using var leaderElection = CreateSUT(options);

        var taskExecuted = false;

        // Act
        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(10));

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
}