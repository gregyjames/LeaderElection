using System.Diagnostics;

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

    protected static async Task WaitForLeadershipChange(
        ILeaderElection leaderElection,
        bool expectedLeadership,
        TimeSpan timeout = default
    )
    {
        if (timeout == TimeSpan.Zero)
            timeout = TimeSpan.FromSeconds(30);

        var tcs = new TaskCompletionSource<bool>();

        void Handler(object? sender, LeadershipChangedEventArgs leadership)
        {
            if (leadership.IsLeader == expectedLeadership)
            {
                tcs.TrySetResult(true);
            }
        }

        Debug.Assert(leaderElection != null, nameof(leaderElection) + " != null");
        leaderElection.LeadershipChanged += Handler;
        try
        {
            // Check if already in the expected state
            if (leaderElection.IsLeader == expectedLeadership)
            {
                tcs.TrySetResult(true);
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token,
                CancellationToken
            );
            linkedCts.Token.Register(() => tcs.TrySetCanceled());

            await tcs.Task;
        }
        finally
        {
            leaderElection.LeadershipChanged -= Handler;
        }
    }

    protected static async Task WaitForError(
        ILeaderElection leaderElection,
        TimeSpan timeout = default
    )
    {
        if (timeout == TimeSpan.Zero)
            timeout = TimeSpan.FromSeconds(30);

        var tcs = new TaskCompletionSource<Exception>();

        void Handler(object? sender, LeadershipExceptionEventArgs args) =>
            tcs.TrySetResult(args.LeadershipException);

        Debug.Assert(leaderElection != null, nameof(leaderElection) + " != null");
        leaderElection.ErrorOccurred += Handler;

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token,
                CancellationToken
            );
            linkedCts.Token.Register(() => tcs.TrySetCanceled());

            await tcs.Task;
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
    /// <param name="timeout">The maximum time to wait for a renewal.</param>
    /// <param name="pollInterval">The interval at which to check for leadership renewal.</param>
    /// <returns>True if a renewal was observed within the timeout period; otherwise, false.</returns>
    protected async Task<bool> WaitForLeadershipRenewal(
        ILeaderElection leaderElection,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null
    )
    {
        timeout ??= TimeSpan.FromSeconds(30);
        pollInterval ??= TimeSpan.FromMilliseconds(50);

        Debug.Assert(leaderElection != null, nameof(leaderElection) + " != null");
        leaderElection.IsLeader.Should().BeTrue("Expected to be leader before waiting for renewal");
        var lastKnownRenewal = leaderElection.LastLeadershipRenewal;

        var stopTime = TimeProvider.GetUtcNow() + timeout.Value;
        while (TimeProvider.GetUtcNow() < stopTime)
        {
            await TimeProvider.Delay(pollInterval.Value, CancellationToken);

            if (!leaderElection.IsLeader)
            {
                return false; // Leadership lost
            }

            if (leaderElection.LastLeadershipRenewal > lastKnownRenewal)
            {
                return true; // Renewal observed
            }
        }

        return false; // Timeout reached without observing renewal
    }

    protected async Task TestShouldRetainLeadershipAfterAtLeastOneRenewalCycle(
        ILeaderElection leaderElection,
        LeaderElectionSettingsBase settings
    )
    {
        // Act
        Debug.Assert(leaderElection != null, nameof(leaderElection) + " != null");
        await leaderElection.StartAsync(CancellationToken);
        await WaitForLeadershipChange(leaderElection, true);

        Debug.Assert(settings != null, nameof(settings) + " != null");
        var renewalObserved = await WaitForLeadershipRenewal(
            leaderElection,
            settings.RenewInterval + TimeSpan.FromSeconds(0.5) // Add a buffer to avoid timing issues
        );

        // Assert
        renewalObserved.Should().BeTrue();

        await leaderElection.StopAsync(CancellationToken);
    }
}
