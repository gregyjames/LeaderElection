using System;
using System.Threading;
using System.Threading.Tasks;

namespace LeaderElection;

/// <summary>
/// Common interface for leader election implementations
/// </summary>
public interface ILeaderElection : IAsyncDisposable
{
    /// <summary>
    /// Gets whether this instance is currently the leader
    /// </summary>
    bool IsLeader { get; }

    DateTime LastLeadershipRenewal { get; }

    /// <summary>
    /// Event fired when leadership status changes
    /// </summary>
    event EventHandler<bool>? LeadershipChanged;

    /// <summary>
    /// Event fired when an error occurs during leader election
    /// </summary>
    event EventHandler<Exception>? ErrorOccurred;

    /// <summary>
    /// Starts the leader election process
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the leader election process
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire leadership manually
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if leadership was acquired, false otherwise</returns>
    Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a task only if this instance is the leader
    /// </summary>
    /// <param name="task">The task to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task RunTaskIfLeaderAsync(Func<Task> task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a synchronous task only if this instance is the leader
    /// </summary>
    /// <param name="task">The task to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task RunTaskIfLeaderAsync(Action task, CancellationToken cancellationToken = default);
} 