using Microsoft.Extensions.Time.Testing;

namespace LeaderElection.Tests;

public class LeaderElectionBaseTests
{
    readonly FakeTimeProvider _fakeTimeProvider = new();

    // Helper to create a new instance of the SUT with optional settings and random double generator
    // The random double generator defaults to 0.5 which results in no jitter.
    FakeLeaderElection CreateSut(
        FakeLeaderElectionSettings? settings = null,
        Func<double>? getRandomDouble = null
    ) => new(settings ?? new(), _fakeTimeProvider, getRandomDouble ?? (() => 0.5));

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

    // Helper to calculate the expected leader-loop retry interval based on
    // settings, error count, and the "random" double.
    static TimeSpan ExpectedRetryInterval(
        FakeLeaderElectionSettings settings,
        int errorCount,
        double randomDouble
    ) =>
        TimeSpan.FromTicks(
            Math.Min(
                (long)(
                    settings.RetryInterval.Ticks
                    * Math.Pow(settings.RetryBackoffFactor, Math.Min(errorCount, 30))
                    * (1 - settings.RetryJitter + (randomDouble * settings.RetryJitter * 2))
                ),
                settings.MaxRetryInterval.Ticks
            )
        );

    [Fact]
    public void ExpectedRetryIntervalShouldCalculateCorrectly()
    {
        // Arrange
        var settings = new FakeLeaderElectionSettings
        {
            RetryInterval = TimeSpan.FromSeconds(1),
            RetryJitter = 0.50, // ±50%, depending on random double
            RetryBackoffFactor = 2.0,
            // we want to max out on 4th failure, so max should be < (1s * 2^4 * (1 ± jitter)) or 8-24s
            MaxRetryInterval = TimeSpan.FromSeconds(7.9),
        };

        // Assert
        // verify base retry interval
        ExpectedRetryInterval(settings, 0, 0.0).Should().Be(TimeSpan.FromSeconds(0.5)); // 1s base retry, -50% jitter
        ExpectedRetryInterval(settings, 0, 0.5).Should().Be(TimeSpan.FromSeconds(1.0)); // 1s base retry, -0% jitter
        ExpectedRetryInterval(settings, 0, 1.0).Should().Be(TimeSpan.FromSeconds(1.5)); // 1s base retry, +50% jitter

        // verify first retry interval
        ExpectedRetryInterval(settings, 1, 0.0).Should().Be(TimeSpan.FromSeconds(1)); // 2s backoff, -50% jitter
        ExpectedRetryInterval(settings, 1, 0.5).Should().Be(TimeSpan.FromSeconds(2)); // 2s backoff, -0% jitter
        ExpectedRetryInterval(settings, 1, 1.0).Should().Be(TimeSpan.FromSeconds(3)); // 2s backoff, +50% jitter

        // verify second retry interval
        ExpectedRetryInterval(settings, 2, 0.0).Should().Be(TimeSpan.FromSeconds(2)); // 4s backoff, -50% jitter
        ExpectedRetryInterval(settings, 2, 0.5).Should().Be(TimeSpan.FromSeconds(4)); // 4s backoff, -0% jitter
        ExpectedRetryInterval(settings, 2, 1.0).Should().Be(TimeSpan.FromSeconds(6)); // 4s backoff, +50% jitter

        // verify third retry interval
        ExpectedRetryInterval(settings, 3, 0.0).Should().Be(TimeSpan.FromSeconds(4)); // 8s backoff, -50% jitter
        ExpectedRetryInterval(settings, 3, 0.5).Should().Be(TimeSpan.FromSeconds(7.9)); // 8s backoff, -0% jitter = 8s but max is 7.9s
        ExpectedRetryInterval(settings, 3, 1.0).Should().Be(TimeSpan.FromSeconds(7.9)); // 8s backoff, +50% jitter = 12s but max is 7.9s

        // verify the expected retry intervals maxes out correctly
        ExpectedRetryInterval(settings, 4, 0.0).Should().Be(TimeSpan.FromSeconds(7.9)); // 16s backoff, -50% jitter = 8s but max is 7.9s
        ExpectedRetryInterval(settings, 5, 0.0).Should().Be(TimeSpan.FromSeconds(7.9)); // 32s backoff, -50% jitter = 16s but max is 7.9s
        ExpectedRetryInterval(settings, 30, 0.0).Should().Be(TimeSpan.FromSeconds(7.9)); // max is 7.9s
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
        await FastForward(sut.Settings.RetryInterval * sut.Settings.RetryBackoffFactor); // backoff should be applied

        // Assert
        sut.IsLeader.Should().BeTrue();
        sut.LastLeadershipRenewal.Should().Be(_fakeTimeProvider.GetUtcNow().UtcDateTime);
        sut.Settings.TryAcquireCount.Should().Be(2);
        sut.Settings.LeadershipChangedCount.Should().Be(1);
        sut.Settings.ErrorCount.Should().Be(1);
    }

    [Fact]
    public async Task LeaderLoopShouldApplyBackoffAndJitterCorrectly()
    {
        // Arrange
        await using var sut = CreateSut(getRandomDouble: () => 0.0);

        // calculates the expected retry interval based on error count
        TimeSpan expectedRetryInterval(int errorCount) =>
            ExpectedRetryInterval(sut.Settings, errorCount, 0.0);

        // simulate acquisition failure to trigger retries
        sut.Settings.AcquireResult = () => throw new InvalidOperationException("Test exception");

        // Act
        await sut.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        // simulate multiple retries
        for (var attempt = 0; attempt < 32; attempt++)
        {
            await FastForward(expectedRetryInterval(attempt));
            sut.Settings.TryAcquireCount.Should().Be(attempt + 1);
            sut.Settings.ErrorCount.Should().Be(attempt + 1);
        }
    }

    [Fact]
    public async Task LeaderLoopRetryIntervalResetsAfterNonFatalAcquisition()
    {
        // Arrange
        await using var sut = CreateSut(getRandomDouble: () => 0.0);

        TimeSpan expectedRetryInterval(int errorCount) =>
            ExpectedRetryInterval(sut.Settings, errorCount, 0.0);

        // simulate multiple failed retries to increase backoff...
        sut.Settings.AcquireResult = () => throw new InvalidOperationException("Test exception");
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await FastForward(expectedRetryInterval(0));
        await FastForward(expectedRetryInterval(1));

        // Act - simulate successful retry (but no leadership)
        sut.Settings.AcquireResult = () => false;
        await FastForward(expectedRetryInterval(2));
        sut.Settings.TryAcquireCount.Should().Be(3);
        sut.Settings.ErrorCount.Should().Be(2); // last attempt did not error

        // Assert - retry interval should be back to base value
        await FastForward(expectedRetryInterval(0));
        sut.Settings.TryAcquireCount.Should().Be(4);
        // same for subsequent attempts...
        await FastForward(expectedRetryInterval(0));
        sut.Settings.TryAcquireCount.Should().Be(5);
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
