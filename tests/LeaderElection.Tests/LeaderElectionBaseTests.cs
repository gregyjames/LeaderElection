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
        // small delay to ensure OS has time to unblock any tasks waiting for
        // the Leader Loop semaphore before advancing time
        await Task.Delay(5);
        _fakeTimeProvider.Advance(timeSpan);
        // allow any released tasks to update state before assertions.
        await Task.Delay(1);
    }

    async Task StartAsync(FakeLeaderElection sut)
    {
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(TimeSpan.Zero);
    }

    [Fact]
    public async Task StartAsyncShouldAcquireLeadership()
    {
        // Arrange
        await using var sut = CreateSut();

        // Act
        await StartAsync(sut);

        // Assert
        sut.IsLeader.Should().BeTrue();
        sut.LastLeadershipRenewal.Should().Be(_fakeTimeProvider.GetUtcNow().UtcDateTime);
        sut.Settings.TryAcquireCount.Should().Be(1);
        sut.Settings.LeadershipChangedCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);

        // Call StartAsync again should be no-op
        await StartAsync(sut);
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
        await StartAsync(sut);
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
        await StartAsync(sut);

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
    public async Task StartAsyncCancellationTokenShouldHaveNoEffectOnLeaderLoop()
    {
        // Arrange
        await using var sut = CreateSut();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );

        sut.Settings.AcquireResult = () =>
        {
            cts.Cancel();
            return true;
        };

        // Act
        await sut.StartAsync(cts.Token);
        await FastForward(TimeSpan.Zero);

        // Assert - Should have acquired leadership and not been cancelled
        cts.IsCancellationRequested.Should().BeTrue();
        sut.IsLeader.Should().BeTrue();
        sut.Settings.TryAcquireCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);

        await FastForward(sut.Settings.RenewInterval);
        sut.Settings.ErrorCount.Should().Be(0);
        sut.Settings.TryRenewCount.Should().Be(1);
    }

    [Fact]
    public async Task RenewShouldBeCalledRepeatedlyWhenLeader()
    {
        // Arrange
        await using var sut = CreateSut();

        await StartAsync(sut);
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
        await StartAsync(sut);
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
    public async Task TryAcquireLeadershipAsyncShouldThrowWhenLeaderLoopNotRunning()
    {
        // Arrange
        await using var sut = CreateSut();
        sut.LeaderLoopRunning.Should().BeFalse();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TryAcquireLeadershipAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task TryAcquireLeadershipAsyncShouldReturnFalseWhenNotLeader()
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.AcquireResult = () => false; // ensure we don't acquire leadership
        await StartAsync(sut);

        sut.LeaderLoopRunning.Should().BeTrue();
        sut.IsLeader.Should().BeFalse();

        // Act
        var result = await sut.TryAcquireLeadershipAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeFalse();
        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquireLeadershipAsyncShouldReturnTrueWhenAlreadyLeader()
    {
        // Arrange
        await using var sut = CreateSut();
        await StartAsync(sut);
        sut.IsLeader.Should().BeTrue();

        // Act
        var result = await sut.TryAcquireLeadershipAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeTrue();
        sut.IsLeader.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireLeadershipAsyncShouldAcquireLeadershipWhenNotLeader()
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.AcquireResult = () => false; // ensure we don't acquire leadership
        await StartAsync(sut);
        sut.LeaderLoopRunning.Should().BeTrue();
        sut.IsLeader.Should().BeFalse();

        sut.Settings.AcquireResult = () => true; // ensure we can acquire leadership

        // Act
        var result = await sut.TryAcquireLeadershipAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeTrue();
        sut.IsLeader.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryAcquireLeadershipAsyncDoesNotThrowWhenAcquireThrows(bool cancel)
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.AcquireResult = () => false; // ensure we don't acquire leadership
        await StartAsync(sut);
        sut.LeaderLoopRunning.Should().BeTrue();
        sut.IsLeader.Should().BeFalse();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );

        sut.Settings.AcquireResult = () =>
        {
            if (cancel)
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("Test exception");
        };

        // Act
        var result = await sut.TryAcquireLeadershipAsync(cts.Token);

        // Assert
        result.Should().BeFalse();
        sut.IsLeader.Should().BeFalse();
        sut.Settings.ErrorCount.Should().Be(1);
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
        await StartAsync(sut);
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
        await StartAsync(sut);
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
    public async Task RunTaskIfLeaderAsyncShouldNotReportErrorOnException()
    {
        // Arrange
        await using var sut = CreateSut();

        // start with leadership
        sut.Settings.AcquireResult = () => true;
        await StartAsync(sut);
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
        sut.Settings.ErrorCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopAsyncShouldAlwaysReleaseLeadership(bool gracefulShutdown)
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.EnableGracefulShutdown = gracefulShutdown;

        await StartAsync(sut);
        sut.IsLeader.Should().BeTrue();

        // Act
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.IsLeader.Should().BeFalse(); // because stopped
        sut.LeaderLoopRunning.Should().BeFalse();
        // should always release on stop, regardless of graceful shutdown setting
        sut.Settings.TryReleaseCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task StopAsyncShouldNotThrowReleaseException()
    {
        // Arrange
        await using var sut = CreateSut();
        await StartAsync(sut);
        sut.IsLeader.Should().BeTrue();

        sut.Settings.ReleaseAction = () => throw new InvalidOperationException("Test exception");

        // Act
        await sut.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        sut.IsLeader.Should().BeFalse(); // stopped
        sut.LeaderLoopRunning.Should().BeFalse();
        sut.Settings.TryReleaseCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(1);
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
    public async Task DisposeAsyncShouldReleaseLeadershipWhenGracefulShutdownEnabled(
        bool gracefulShutdown
    )
    {
        // Arrange
        await using var sut = CreateSut();
        sut.Settings.EnableGracefulShutdown = gracefulShutdown;

        await StartAsync(sut);
        sut.IsLeader.Should().BeTrue();

        // Act
        await sut.DisposeAsync();

        // Assert
        sut.IsLeader.Should().BeFalse(); // because disposed/stopped
        sut.LeaderLoopRunning.Should().BeFalse();
        sut.Settings.TryReleaseCount.Should().Be(gracefulShutdown ? 1 : 0);
        sut.Settings.ErrorCount.Should().Be(0);
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
        sut.LeadershipChanged += (_, _) => throw new InvalidOperationException("Client exception");

        await StartAsync(sut);
        sut.IsLeader.Should().BeTrue();

        // Act
        await sut.DisposeAsync();

        // Assert
        sut.IsLeader.Should().BeFalse(); // because disposed/stopped
        sut.Settings.TryReleaseCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(0);
    }
}
