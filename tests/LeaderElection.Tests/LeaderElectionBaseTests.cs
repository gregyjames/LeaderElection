using Microsoft.Extensions.Time.Testing;

namespace LeaderElection.Tests;

public class LeaderElectionBaseTests
{
    readonly FakeTimeProvider _fakeTimeProvider = new();

    FakeLeaderElection CreateSut(FakeLeaderElectionSettings? settings = null) =>
        new(settings ?? new(), _fakeTimeProvider);

    // Helper method to fast forward time and allow any released tasks to run
    async Task FastForward(TimeSpan timeSpan)
    {
        _fakeTimeProvider.Advance(timeSpan);
        await Task.Yield(); // allow any released tasks to run
    }

    [Fact]
    public async Task StartAsyncShouldAcquireLeadership()
    {
        // Arrange
        await using var sut = CreateSut();

        // Act
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);

        // Assert
        sut.IsLeader.Should().BeTrue();
        sut.LastLeadershipRenewal.Should().Be(_fakeTimeProvider.GetUtcNow().UtcDateTime);
        sut.Settings.TryAcquireCount.Should().Be(1);
        sut.Settings.LeadershipChangedCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);

        // Call StartAsync again should be no-op
        await sut.StartAsync(TestContext.Current.CancellationToken);
        sut.IsLeader.Should().BeTrue();
        sut.Settings.TryAcquireCount.Should().Be(1);
        sut.Settings.LeadershipChangedCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsyncShouldRetryAcquisitionWhenFails()
    {
        // Arrange
        await using var sut = CreateSut();

        sut.Settings.AcquireResult = () => false;
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeFalse();
        sut.Settings.TryAcquireCount.Should().Be(1);
        sut.Settings.LeadershipChangedCount.Should().Be(0);
        sut.Settings.ErrorCount.Should().Be(0);

        // Act
        sut.Settings.AcquireResult = () => true;
        await FastForward(sut.Settings.RetryInterval);

        // Assert
        sut.IsLeader.Should().BeTrue();
        sut.LastLeadershipRenewal.Should().Be(_fakeTimeProvider.GetUtcNow().UtcDateTime);
        sut.Settings.TryAcquireCount.Should().Be(2);
        sut.Settings.LeadershipChangedCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task LeaderLoopShouldRetryOnError()
    {
        // Arrange
        await using var sut = CreateSut();

        // Act
        sut.Settings.AcquireResult = () => throw new InvalidOperationException("Test exception");
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);

        // Assert - Should have reported the error
        sut.IsLeader.Should().BeFalse();
        sut.Settings.TryAcquireCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(1);

        // Act - Clear error and retry
        sut.Settings.AcquireResult = () => true;
        await FastForward(sut.Settings.RetryInterval);

        // Assert
        sut.IsLeader.Should().BeTrue();
        sut.LastLeadershipRenewal.Should().Be(_fakeTimeProvider.GetUtcNow().UtcDateTime);
        sut.Settings.TryAcquireCount.Should().Be(2);
        sut.Settings.LeadershipChangedCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(1);
    }

    [Fact]
    public async Task LeaderLoopShouldCancelOnRequest()
    {
        // Arrange
        await using var sut = CreateSut();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );

        sut.Settings.AcquireResult = () =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return true;
        };

        // Act
        await sut.StartAsync(cts.Token);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeFalse();
        sut.Settings.TryAcquireCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task RenewShouldBeCalledRepeatedlyWhenLeader()
    {
        // Arrange
        await using var sut = CreateSut();

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeTrue();
        sut.Settings.LeadershipChangedCount.Should().Be(1);

        // Act
        // Wait for multiple renewals
        for (var i = 0; i < 50; i++)
        {
            await FastForward(sut.Settings.RenewInterval);

            // Assert
            sut.IsLeader.Should().BeTrue();
            sut.Settings.TryRenewCount.Should().Be(i + 1);
        }

        sut.Settings.LeadershipChangedCount.Should().Be(1);
    }

    [Fact]
    public async Task LeadershipShouldBeLostWhenRenewalFails()
    {
        // Arrange
        await using var sut = CreateSut();

        // acquire leadership first
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeTrue();
        sut.Settings.TryRenewCount.Should().Be(0);
        sut.Settings.LeadershipChangedCount.Should().Be(1);

        // Act - Renewal failure
        sut.Settings.RenewResult = () => false;
        await FastForward(sut.Settings.RenewInterval);

        // Assert - Lost leadership
        sut.IsLeader.Should().BeFalse();
        sut.Settings.TryRenewCount.Should().Be(1);
        sut.Settings.LeadershipChangedCount.Should().Be(2);
        sut.Settings.ErrorCount.Should().Be(0);

        // Act - Clear error and retry
        sut.Settings.RenewResult = () => true;
        await FastForward(sut.Settings.RetryInterval);

        // Assert - Leadership reacquired
        sut.IsLeader.Should().BeTrue();
        sut.Settings.TryAcquireCount.Should().Be(2);
        sut.Settings.TryRenewCount.Should().Be(1);
        sut.Settings.LeadershipChangedCount.Should().Be(3);
        sut.Settings.ErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task RunTaskIfLeaderAsyncShouldNotRunWhenNotStarted()
    {
        // Arrange
        await using var sut = CreateSut();

        // Act
        var taskExecuted = false;
        await sut.RunTaskIfLeaderAsync(
            () => taskExecuted = true,
            TestContext.Current.CancellationToken
        );

        // Assert
        taskExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task RunTaskIfLeaderAsyncShouldNotRunWhenNotLeader()
    {
        // Arrange
        await using var sut = CreateSut();

        // start without leadership
        sut.Settings.AcquireResult = () => false;
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeFalse();

        // Act
        var taskExecuted = false;
        await sut.RunTaskIfLeaderAsync(
            () => taskExecuted = true,
            TestContext.Current.CancellationToken
        );

        // Assert
        taskExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task RunTaskIfLeaderAsyncShouldRunWhenLeader()
    {
        // Arrange
        await using var sut = CreateSut();

        // start with leadership
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeTrue();

        // Act
        var taskExecuted = false;
        await sut.RunTaskIfLeaderAsync(
            () => taskExecuted = true,
            TestContext.Current.CancellationToken
        );

        // Assert
        taskExecuted.Should().BeTrue();
    }

    [Fact]
    public async Task RunTaskIfLeaderAsyncShouldReportErrorOnException()
    {
        // Arrange
        await using var sut = CreateSut();

        // start with leadership
        sut.Settings.AcquireResult = () => true;
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeTrue();
        sut.Settings.ErrorCount.Should().Be(0);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunTaskIfLeaderAsync(
                () => throw new InvalidOperationException("Test exception"),
                TestContext.Current.CancellationToken
            )
        );

        // Assert
        sut.Settings.ErrorCount.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopAsyncShouldReleaseLeadership(bool gracefulShutdown)
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.EnableGracefulShutdown = gracefulShutdown;

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeTrue();

        // Act
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.IsLeader.Should().BeFalse(); // because stopped
        sut.Settings.TryReleaseCount.Should().Be(gracefulShutdown ? 1 : 0);
    }

    [Fact]
    public async Task StopAsyncShouldAlwaysStopLeaderLoop()
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.AcquireResult = () => false; // do NOT acquire leadership
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeFalse();
        sut.LeaderLoopRunning.Should().BeTrue();

        // Act
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.LeaderLoopRunning.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsyncShouldReleaseLeadership(bool gracefulShutdown)
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.EnableGracefulShutdown = gracefulShutdown;

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeTrue();

        // Act
        await sut.DisposeAsync();

        // Assert
        sut.IsLeader.Should().BeFalse(); // because disposed/stopped
        sut.Settings.TryReleaseCount.Should().Be(gracefulShutdown ? 1 : 0);
    }

    [Fact]
    public async Task UseAfterDisposeShouldThrowObjectDisposedException()
    {
        // Arrange
        await using var sut = CreateSut();
        await sut.DisposeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sut.StartAsync(TestContext.Current.CancellationToken)
        );
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sut.StopAsync(TestContext.Current.CancellationToken)
        );
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sut.RunTaskIfLeaderAsync(
                () => Task.CompletedTask,
                TestContext.Current.CancellationToken
            )
        );
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sut.RunTaskIfLeaderAsync(() => { }, TestContext.Current.CancellationToken)
        );

        // Properties are okay
        sut.IsLeader.Should().BeFalse();
        sut.LastLeadershipRenewal.Should().Be(DateTime.MinValue);

        // Calling DisposeAsync multiple times should be fine
        await sut.DisposeAsync();
        await sut.DisposeAsync();
    }

    [Fact]
    public async Task DisposeShouldSwallowReleaseException()
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.EnableGracefulShutdown = true;
        sut.Settings.ReleaseAction = () => throw new InvalidOperationException("Release failed");

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
        sut.IsLeader.Should().BeTrue();

        // Act
        await sut.DisposeAsync();

        // Assert
        sut.Settings.TryReleaseCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);
    }
}
