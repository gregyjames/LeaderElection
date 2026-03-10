using Xunit;

namespace LeaderElection.Tests;

public abstract class TestBase : IAsyncLifetime
{
    private readonly CancellationTokenSource _abortTestTokenSource = new();

    /// <summary>
    /// The <see cref="CancellationToken"/> used to abort the test.
    /// Tests should observe this token and gracefully exit when cancellation is requested.
    /// </summary>
    /// <remarks>
    /// In Xunit v3, replace this with `Xunit.TestContext.Current.CancellationToken`.
    /// </remarks>
    protected CancellationToken CancellationToken => _abortTestTokenSource.Token;

    protected TestBase() { }

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual async Task DisposeAsync()
    {
        if (!_abortTestTokenSource.IsCancellationRequested)
            await _abortTestTokenSource.CancelAsync();
        _abortTestTokenSource.Dispose();
    }

    protected async Task WaitForLeadershipChange(
        ILeaderElection leaderElection,
        bool expectedLeadership,
        TimeSpan timeout = default
    )
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(30);

        var tcs = new TaskCompletionSource<bool>();
        var timeoutCts = new CancellationTokenSource(timeout);

        EventHandler<bool>? handler = null;
        handler = (sender, isLeader) =>
        {
            if (isLeader == expectedLeadership)
            {
                tcs.TrySetResult(true);
                leaderElection.LeadershipChanged -= handler;
            }
        };

        leaderElection.LeadershipChanged += handler;

        // Check if already in the expected state
        if (leaderElection.IsLeader == expectedLeadership)
        {
            tcs.TrySetResult(true);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, CancellationToken);
        linkedCts.Token.Register(() => tcs.TrySetCanceled());

        await tcs.Task;
    }

    protected async Task WaitForError(ILeaderElection leaderElection, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = TimeSpan.FromSeconds(30);

        var tcs = new TaskCompletionSource<Exception>();
        var timeoutCts = new CancellationTokenSource(timeout);

        EventHandler<Exception>? handler = null;
        handler = (sender, exception) =>
        {
            tcs.TrySetResult(exception);
            leaderElection.ErrorOccurred -= handler;
        };

        leaderElection.ErrorOccurred += handler;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, CancellationToken);
        linkedCts.Token.Register(() => tcs.TrySetCanceled());

        await tcs.Task;
    }
}