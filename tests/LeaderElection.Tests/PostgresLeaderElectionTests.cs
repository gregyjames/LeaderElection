using LeaderElection.Postgres;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeaderElection.Tests;

[Collection("PostgreSQL Container")]
[Trait("Kind", "Integration")]
[Trait("Category", "PostgreSQL")]
public sealed class PostgresLeaderElectionTests(PostgresContainerFixture postgresFixture) : TestBase
{
    private static long _lockIdCounter = 1000;

    private static long GetNextLockId() => Interlocked.Increment(ref _lockIdCounter);

    private PostgresSettings CreateSettings(
        string? instanceId = null,
        string? connectionString = null,
        long? lockId = null,
        TimeSpan? renewInterval = null,
        TimeSpan? retryInterval = null,
        bool enableGracefulShutdown = true
    ) =>
        new()
        {
            ConnectionString = connectionString ?? postgresFixture.ConnectionString,
            LockId = lockId ?? GetNextLockId(),
            InstanceId = instanceId ?? "test-instance-1",
            RetryInterval = retryInterval ?? TimeSpan.FromSeconds(1),
            RenewInterval = renewInterval ?? TimeSpan.FromSeconds(2),
            EnableGracefulShutdown = enableGracefulShutdown,
        };

    private static PostgresLeaderElection CreateSut(PostgresSettings options) =>
        new(options, NullLoggerFactory.Instance.CreateLogger<PostgresLeaderElection>());

    [Fact]
    public async Task ShouldAcquireLeadershipWhenNoOtherInstanceExists()
    {
        var options = CreateSettings();

        await using var leaderElection = CreateSut(options);

        await leaderElection.StartAsync(CancellationToken);

        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(10));
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldNotAcquireLeadershipWhenAnotherInstanceHasLeadership()
    {
        var options1 = CreateSettings(instanceId: "test-instance-01");
        var options2 = CreateSettings(instanceId: "test-instance-02", lockId: options1.LockId);

        await using var leaderElection1 = CreateSut(options1);
        await using var leaderElection2 = CreateSut(options2);

        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(10));

        await leaderElection2.StartAsync(CancellationToken);
        await TimeProvider.Delay(TimeSpan.FromSeconds(5), CancellationToken);

        leaderElection1.IsLeader.Should().BeTrue();
        leaderElection2.IsLeader.Should().BeFalse();

        await leaderElection1.StopAsync(CancellationToken);
        await leaderElection2.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldTransferLeadershipWhenCurrentLeaderStops()
    {
        var options1 = CreateSettings(instanceId: "test-instance-01");
        var options2 = CreateSettings(instanceId: "test-instance-02", lockId: options1.LockId);

        await using var leaderElection1 = CreateSut(options1);
        await using var leaderElection2 = CreateSut(options2);

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
    public async Task ShouldRunTaskOnlyWhenLeader()
    {
        var options = CreateSettings();

        await using var leaderElection = CreateSut(options);

        var taskExecuted = false;

        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(10));

        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationToken);

        taskExecuted.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldNotRunTaskWhenNotLeader()
    {
        var options = CreateSettings();

        await using var leaderElection = CreateSut(options);

        var taskExecuted = false;

        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationToken);

        taskExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldHandleManualLeadershipAcquisition()
    {
        var options = CreateSettings();

        await using var leaderElection = CreateSut(options);

        var result = await leaderElection.TryAcquireLeadershipAsync(CancellationToken);

        result.Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldRetainLeadershipAfterAtLeastOneRenewalCycle()
    {
        // Arrange
        var options = CreateSettings(renewInterval: TimeSpan.FromSeconds(1));

        await using var leaderElection = CreateSut(options);

        // Act & Assert
        await TestShouldRetainLeadershipAfterAtLeastOneRenewalCycle(leaderElection, options);
    }
}
