namespace LeaderElection;

/// <summary>
/// Common interface for leader election implementations
/// </summary>
public interface ILeaderElection : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the leader loop is currently running, indicating that the
    /// instance is actively participating in the leader election process.
    /// </summary>
    bool LeaderLoopRunning { get; }

    /// <summary>
    /// Gets whether this instance is currently the leader
    /// </summary>
    bool IsLeader { get; }

    /// <summary>
    /// Gets the timestamp of the last successful leadership acquisition or renewal.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="DateTime.MinValue"/> when <see cref="IsLeader"/> is false.
    /// </remarks>
    DateTime LastLeadershipRenewal { get; }

    /// <summary>
    /// Event fired when leadership status changes
    /// </summary>
    event EventHandler<LeadershipChangedEventArgs>? LeadershipChanged;

    /// <summary>
    /// Event fired when an error occurs during leader election
    /// </summary>
    event EventHandler<LeadershipExceptionEventArgs>? ErrorOccurred;

    /// <summary>
    /// Starts the leader election process.
    /// </summary>
    /// <remarks>
    /// Calling this method when the leader loop is already running has no effect.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the leader election instance has been disposed.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the leader election process.
    /// </summary>
    /// <remarks>
    /// Calling this method when the leader loop is not running has no effect.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the leader election instance has been disposed.</exception>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire leadership manually.
    /// </summary>
    /// <remarks>
    /// Must be called while the leader loop is running.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if leadership was acquired, false otherwise</returns>
    /// <exception cref="InvalidOperationException">Thrown if the leader loop is not running.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the leader election instance has been disposed.</exception>
    Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a task only if this instance is the leader.
    /// </summary>
    /// <param name="leaderTask">The task to execute. The function receives a <see cref="CancellationToken"/>
    /// that is canceled if a) leadership is lost (including when the leader loop is stopped) or b) the provided
    /// <paramref name="cancellationToken"/> is canceled.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that resolves to <c>true</c> if the leader task was executed; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the leader task is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the leader election instance has been disposed.</exception>
    Task<bool> RunTaskIfLeaderAsync(
        Func<CancellationToken, Task> leaderTask,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Executes a synchronous task only if this instance is the leader.
    /// </summary>
    /// <param name="leaderAction">The action to execute. The action receives a <see cref="CancellationToken"/>
    /// that is canceled if a) leadership is lost (including when the leader loop is stopped) or b) the provided
    /// <paramref name="cancellationToken"/> is canceled.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that resolves to <c>true</c> if the leader action was executed; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the leader action is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the leader election instance has been disposed.</exception>
    Task<bool> RunTaskIfLeaderAsync(
        Action<CancellationToken> leaderAction,
        CancellationToken cancellationToken = default
    );
}
