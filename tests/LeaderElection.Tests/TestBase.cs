namespace LeaderElection.Tests;

public abstract class TestBase
{
    /// <summary>
    /// The <see cref="CancellationToken"/> used to abort the test.
    /// Tests should observe this token and gracefully exit when cancellation is requested.
    /// </summary>
    protected CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    protected TestBase() { }

    protected async Task WaitForLeadershipChange(
        ILeaderElection leaderElection,
        bool expectedLeadership,
        TimeSpan timeout = default
    )
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(30);

        var tcs = new TaskCompletionSource<bool>();

        EventHandler<LeadershipChangedEventArgs>? handler = (sender, leadershipEvent) =>
        {
            if (leadershipEvent.IsLeader == expectedLeadership)
            {
                tcs.TrySetResult(true);
            }
        };

        leaderElection.LeadershipChanged += handler;
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
            leaderElection.LeadershipChanged -= handler;
        }
    }

    protected async Task WaitForError(ILeaderElection leaderElection, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(30);

        var tcs = new TaskCompletionSource<Exception>();

        EventHandler<LeaderElectionErrorEventArgs>? handler = (sender, leadershipException) => tcs.TrySetResult(leadershipException.Exception);

        leaderElection.ErrorOccurred += handler;
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
            leaderElection.ErrorOccurred -= handler;
        }
    }
}
