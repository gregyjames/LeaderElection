using Microsoft.Extensions.Time.Testing;

namespace LeaderElection.Tests;

public abstract class TestBase
{
    /// <summary>
    /// The <see cref="CancellationToken"/> used to abort the test.
    /// Tests should observe this token and gracefully exit when cancellation is requested.
    /// </summary>
    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// The <see cref="TimeProvider"/> used to control time in tests.
    /// Tests can use <see cref="FakeTimeProvider"/> to simulate time passage and test
    /// time-dependent behavior without real delays.
    /// Defaults to <see cref="TimeProvider.System"/> (real time) if not provided.
    /// </summary>
    protected TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Advances time by the specified <see cref="TimeSpan"/> using the configured
    /// <see cref="TimeProvider"/>. This is a helper method to simulate time passage
    /// in tests that use a <see cref="FakeTimeProvider"/>.
    /// </summary>
    /// <param name="timeSpan">The amount of time to advance/wait.</param>
    protected async Task AdvanceTime(TimeSpan timeSpan)
    {
        if (TimeProvider is FakeTimeProvider fakeTimeProvider)
        {
            // small delay to ensure OS has time to unblock any tasks waiting for
            // the Leader Loop semaphore before advancing time
            await Task.Delay(5, CancellationToken);
            fakeTimeProvider.Advance(timeSpan);
            // allow any released tasks to update state before assertions.
            await Task.Delay(1, CancellationToken);
        }
        else
        {
            await TimeProvider.Delay(timeSpan, CancellationToken);
        }
    }

    /// <summary>
    /// Waits for the specified leader election instance to change to the expected
    /// leadership status.
    /// </summary>
    /// <param name="leaderElection">The leader election instance to monitor for leadership changes.</param>
    /// <param name="expectedLeadership">The expected leadership status to wait for.</param>
    /// <param name="timeout">The maximum time to wait for the leadership change. Defaults to 30 seconds.</param>
    /// <returns>A task that completes when the desired leadership state is reached.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="leaderElection"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the leader election instance is not currently the leader.</exception>
    /// <exception cref="TimeoutException">Thrown if the operation does not complete within the specified timeout.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via the <see cref="CancellationToken"/>.</exception>
    protected async Task WaitForLeadershipChange(
        ILeaderElection leaderElection,
        bool expectedLeadership,
        TimeSpan? timeout = default
    )
    {
        timeout ??= TimeSpan.FromSeconds(30);

        ArgumentNullException.ThrowIfNull(leaderElection);
        if (!leaderElection.LeaderLoopRunning)
        {
            throw new InvalidOperationException("Leader loop is not running.");
        }

        if (leaderElection.IsLeader == expectedLeadership)
        {
            return; // Already in expected state, no need to wait
        }

        var tcs = new TaskCompletionSource();

        void Handler(object? sender, LeadershipChangedEventArgs leadership)
        {
            if (leadership.IsLeader == expectedLeadership)
            {
                tcs.SetResult();
            }
        }

        leaderElection.LeadershipChanged += Handler;
        try
        {
            // Now that we've hooked up the event handler, check if already
            // in the expected state...
            if (leaderElection.IsLeader == expectedLeadership)
            {
                return; // Already in expected state, no need to wait
            }

            await tcs.Task.WaitAsync(timeout.Value, TimeProvider, CancellationToken);
        }
        finally
        {
            leaderElection.LeadershipChanged -= Handler;
        }
    }

    /// <summary>
    /// Waits for an error to be raised by the leader election instance.
    /// </summary>
    /// <param name="leaderElection">The leader election instance to monitor for errors.</param>
    /// <param name="timeout">The maximum time to wait for an error. Defaults to 30 seconds.</param>
    /// <returns>The <see cref="Exception"/> raised by the leader election instance if an error
    /// occurs within the timeout period.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="leaderElection"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the leader election instance is not currently the leader.</exception>
    /// <exception cref="TimeoutException">Thrown if the operation does not complete within the specified timeout.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via the <see cref="CancellationToken"/>.</exception>
    protected async Task<Exception> WaitForError(
        ILeaderElection leaderElection,
        TimeSpan? timeout = default
    )
    {
        timeout ??= TimeSpan.FromSeconds(30);

        ArgumentNullException.ThrowIfNull(leaderElection);
        if (!leaderElection.LeaderLoopRunning)
        {
            throw new InvalidOperationException("Leader loop is not running.");
        }

        var tcs = new TaskCompletionSource<Exception>();

        void Handler(object? sender, LeadershipExceptionEventArgs args) =>
            tcs.TrySetResult(args.LeadershipException);

        leaderElection.ErrorOccurred += Handler;
        try
        {
            return await tcs.Task.WaitAsync(timeout.Value, TimeProvider, CancellationToken);
        }
        finally
        {
            leaderElection.ErrorOccurred -= Handler;
        }
    }

    /// <summary>
    /// Waits for the leader election instance to perform a leadership renewal.
    /// </summary>
    /// <remarks>
    /// The instance must already be the leader before calling this method.
    /// It will monitor the <see cref="ILeaderElection.LastLeadershipRenewal"/> property
    /// and the <see cref="ILeaderElection.IsLeader"/> status, and will return as soon as
    /// either a renewal is observed, leadership is lost, or the specified timeout is reached.
    /// If leadership is lost before a renewal is observed, the method will return false.
    /// If a renewal is observed within the timeout period, it will return true.
    /// If the timeout is reached without observing a renewal or losing leadership, it will
    /// also return false.
    /// </remarks>
    /// <param name="leaderElection">The leader election instance to monitor.</param>
    /// <param name="timeout">The maximum time to wait for a renewal. Defaults to 30 seconds.</param>
    /// <param name="pollInterval">The interval at which to check for leadership renewal. Defaults to 100ms.</param>
    /// <returns>True if a renewal was observed within the timeout period; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="leaderElection"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the leader election instance is not currently the leader.</exception>
    /// <exception cref="TimeoutException">Thrown if the operation does not complete within the specified timeout.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via the <see cref="CancellationToken"/>.</exception>
    protected async Task<bool> WaitForLeadershipRenewal(
        ILeaderElection leaderElection,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null
    )
    {
        timeout ??= TimeSpan.FromSeconds(30);
        pollInterval ??= TimeSpan.FromMilliseconds(100);

        ArgumentNullException.ThrowIfNull(leaderElection);

        var lastKnownRenewal = leaderElection.LastLeadershipRenewal;
        if (!leaderElection.IsLeader)
        {
            throw new InvalidOperationException("Expected to be leader before waiting for renewal");
        }
        lastKnownRenewal.Should().BeAfter(DateTime.MinValue);

        var stopTime = TimeProvider.GetUtcNow() + timeout.Value;
        while (TimeProvider.GetUtcNow() < stopTime)
        {
            await AdvanceTime(pollInterval.Value);

            if (!leaderElection.IsLeader)
            {
                return false; // Leadership lost
            }

            if (leaderElection.LastLeadershipRenewal > lastKnownRenewal)
            {
                return true; // Renewal observed
            }
        }

        // Timeout reached without observing renewal
        throw new TimeoutException(
            $"Did not observe leadership renewal within the expected timeout of {timeout.Value}."
        );
    }

    protected async Task TestShouldRetainLeadershipAfterAtLeastOneRenewalCycle(
        ILeaderElection leaderElection,
        LeaderElectionSettingsBase settings
    )
    {
        ArgumentNullException.ThrowIfNull(leaderElection);
        ArgumentNullException.ThrowIfNull(settings);

        // Act
        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true);

        var renewalObserved = await WaitForLeadershipRenewal(
            leaderElection,
            settings.RenewInterval * 2 // double to avoid timing issues
        );

        // Assert
        renewalObserved.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }
}
