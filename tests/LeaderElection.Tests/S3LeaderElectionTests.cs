using LeaderElection.S3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio.DataModel.Args;

namespace LeaderElection.Tests;

[Collection("Minio Container")]
[Trait("Kind", "Integration")]
[Trait("Category", "S3")]
public sealed class S3LeaderElectionTests(MinioContainerFixture minioFixture) : TestBase
{
    private const string BUCKET_NAME = "leader-election";

    private static S3Settings CreateSettings(
        string objectKey,
        string instanceId = "test-instance-1",
        TimeSpan? leaseDuration = null,
        TimeSpan? renewInterval = null,
        TimeSpan? retryInterval = null
    ) =>
        new()
        {
            BucketName = BUCKET_NAME,
            ObjectKey = objectKey,
            InstanceId = instanceId,
            LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(10),
            RenewInterval = renewInterval ?? TimeSpan.FromSeconds(2),
            RetryInterval = retryInterval ?? TimeSpan.FromSeconds(1),
        };

    private S3LeaderElection CreateSut(S3Settings options) =>
        new(
#pragma warning disable CA2000 // dispose object
            minioFixture.CreateClient(),
#pragma warning restore CA2000
            Options.Create(options),
            NullLoggerFactory.Instance.CreateLogger<S3LeaderElection>()
        );

    private async Task EnsureBucketExistsAsync()
    {
#pragma warning disable CA2000 // dispose object
        var client = minioFixture.CreateClient();
#pragma warning restore CA2000
        if (
            !await client
                .BucketExistsAsync(new BucketExistsArgs().WithBucket(BUCKET_NAME))
                .ConfigureAwait(false)
        )
        {
            await client
                .MakeBucketAsync(new MakeBucketArgs().WithBucket(BUCKET_NAME))
                .ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task ShouldAcquireLeadershipWhenNoOtherInstanceExists()
    {
        // Arrange
        await EnsureBucketExistsAsync();
        var options = CreateSettings("test-leader-election");

        await using var leaderElection = CreateSut(options);

        // Act
        await leaderElection.StartAsync(CancellationToken);

        // Assert
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(15));
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldNotAcquireLeadershipWhenAnotherInstanceHasLeadership()
    {
        // Arrange
        await EnsureBucketExistsAsync();
        var key = "test-leader-election-conflict";
        var options1 = CreateSettings(key, leaseDuration: TimeSpan.FromSeconds(30));
        var options2 = CreateSettings(
            key,
            "test-instance-2",
            leaseDuration: TimeSpan.FromSeconds(30)
        );

        await using var leaderElection1 = CreateSut(options1);
        await using var leaderElection2 = CreateSut(options2);

        // Act
        await leaderElection1.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection1, true, TimeSpan.FromSeconds(15));

        await leaderElection2.StartAsync(CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken);

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
        await EnsureBucketExistsAsync();
        var key = "test-leader-election-transfer";
        var options1 = CreateSettings(
            key,
            leaseDuration: TimeSpan.FromSeconds(5),
            renewInterval: TimeSpan.FromSeconds(1)
        );
        var options2 = CreateSettings(
            key,
            "test-instance-2",
            leaseDuration: TimeSpan.FromSeconds(5),
            renewInterval: TimeSpan.FromSeconds(1)
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
        await EnsureBucketExistsAsync();
        var options = CreateSettings("test-leader-election-task");

        await using var leaderElection = CreateSut(options);
        var taskExecuted = false;

        // Act
        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true, TimeSpan.FromSeconds(15));

        await leaderElection.RunTaskIfLeaderAsync(() => taskExecuted = true, CancellationToken);

        // Assert
        taskExecuted.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldHandleManualLeadershipAcquisition()
    {
        // Arrange
        await EnsureBucketExistsAsync();
        var options = CreateSettings("test-leader-election-manual");

        await using var leaderElection = CreateSut(options);

        // Act
        var result = await leaderElection.TryAcquireLeadershipAsync(CancellationToken);

        // Assert
        result.Should().BeTrue();
        leaderElection.IsLeader.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }

    [Fact]
    public async Task ShouldRetainLeadershipAfterAtLeastOneRenewalCycle()
    {
        // Arrange
        await EnsureBucketExistsAsync();
        var options = CreateSettings(
            "test-leader-election-renewal",
            leaseDuration: TimeSpan.FromSeconds(3),
            renewInterval: TimeSpan.FromSeconds(1)
        );

        await using var leaderElection = CreateSut(options);

        // Act & Assert
        await TestShouldRetainLeadershipAfterAtLeastOneRenewalCycle(leaderElection, options);
    }
}
