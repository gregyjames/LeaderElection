namespace LeaderElection;

/// <summary>
/// Provides Task related polyfills for older versions of .NET.
/// </summary>
internal static class TaskExtensions
{
#if !NET8_0_OR_GREATER
    /// <inheritdoc cref="CancellationTokenSource.CancelAsync"/>
    public static Task CancelAsync(this CancellationTokenSource cancellationTokenSource)
    {
        cancellationTokenSource.Cancel();
        return Task.CompletedTask;
    }
#endif

#if !NET6_0_OR_GREATER
    /// <inheritdoc cref="Task.WaitAsync(CancellationToken)"/>
    public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
            return await task;

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (
            cancellationToken.Register(
                s => ((TaskCompletionSource<T>)s!).TrySetCanceled(cancellationToken),
                tcs
            )
        )
        {
            var completedTask = await Task.WhenAny(task, tcs.Task);
            return await completedTask;
        }
    }

    /// <inheritdoc cref="Task.WaitAsync(CancellationToken)"/>
    public static async Task WaitAsync(this Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            await task;
            return;
        }

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using (
            cancellationToken.Register(
                s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(cancellationToken),
                tcs
            )
        )
        {
            var completedTask = await Task.WhenAny(task, tcs.Task);
            await completedTask;
        }
    }
#endif
}
