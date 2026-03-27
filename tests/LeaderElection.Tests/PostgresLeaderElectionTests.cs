using LeaderElection.Postgres;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeaderElection.Tests;

[Collection("PostgreSQL Container")]
[Trait("Kind", "Integration")]
[Trait("Category", "PostgreSQL")]
public sealed class PostgresLeaderElectionTests(PostgresContainerFixture postgresFixture) : TestBase
{
    private static long _lockIdCounter = 1000;
    private static long GetNextLockId() => Interlocked.Increment(ref _lockIdCounter);

    private PostgresSettings CreateSettings(
        string? connectionString = null,
        long? lockId = null,
        string instanceId = "test-instance-1",
        TimeSpan? retryInterval = null,
        bool enableGracefulShutdown = true
    ) =>
        new()
        {
            ConnectionString = connectionString ?? postgresFixture.ConnectionString,
            LockId = lockId ?? GetNextLockId(),
            InstanceId = instanceId,
            RetryInterval = retryInterval ?? TimeSpan.FromSeconds(1),
            EnableGracefulShutdown = enableGracefulShutdown,
        };

    private PostgresLeaderElection CreateSUT(PostgresSettings options) =>
        new(
            Options.Create(options),
            NullLoggerFactory.Instance.CreateLogger<PostgresLeaderElection>()
        );

    [Fact]
    public async Task Should_Acquire_Leadership_When_No_Other_Instance_Exists()
    {
        var options = CreateSettings();

        await using var leaderElection = CreateSUT(options);

        await leaderElection.StartAsync(CancellationToken);

        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(10));
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Not_Acquire_Leadership_When_Another_Instance_Has_Leadership()
    {
        var options1 = CreateSettings("test-instance-01");
        var options2 = CreateSettings("test-instance-02", lockId: options1.LockId);

        await using var leaderElection1 = CreateSUT(options1);
        await using var leaderElection2 = CreateSUT(options2);

        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(10));

        await leaderElection2.StartAsync(CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken);

        leaderElection1.IsLeader.Should().BeTrue();
        leaderElection2.IsLeader.Should().BeFalse();

        await leaderElection1.StopAsync(CancellationToken);
        await leaderElection2.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Transfer_Leadership_When_Current_Leader_Stops()
    {
        var options1 = CreateSettings("test-instance-01");
        var options2 = CreateSettings("test-instance-02", lockId: options1.LockId);

        await using var leaderElection1 = CreateSUT(options1);
        await using var leaderElection2 = CreateSUT(options2);

        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(10));

        await leaderElection2.StartAsync(CancellationToken);

        await leaderElection1.StopAsync(CancellationToken);

        await WaitForLeadershipChange(leaderElection2, true, TimeSpan.FromSeconds(15));
        leaderElection1.IsLeader.Should().BeFalse();
        leaderElection2.IsLeader.Should().BeTrue();

        await leaderElection2.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Run_Task_Only_When_Leader()
    {
        var options = CreateSettings();

        await using var leaderElection = CreateSUT(options);

        var taskExecuted = false;

        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(10));

        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationToken);

        taskExecuted.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task Should_Not_Run_Task_When_Not_Leader()
    {
        var options = CreateSettings();

        await using var leaderElection = CreateSUT(options);

        var taskExecuted = false;

        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationToken);

        taskExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task Should_Handle_Manual_Leadership_Acquisition()
    {
        var options = CreateSettings();

        await using var leaderElection = CreateSUT(options);

        var result = await leaderElection.TryAcquireLeadershipAsync(CancellationToken);

        result.Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }
}
