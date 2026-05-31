using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace LeaderElection;

/// <summary>
/// Provides necessary polyfills for older versions of .NET.
/// See https://github.com/SimonCropp/Polyfill for implementations.
/// </summary>
[ExcludeFromCodeCoverage]
[System.Diagnostics.DebuggerNonUserCode]
[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Do not alter these implementations"
)]
[SuppressMessage(
    "Performance",
    "CA1849:Call async methods when in an async method",
    Justification = "Do not alter these implementations"
)]
internal static class Polyfill
{
    // csharpier-ignore-start -- contains copied code

    extension(CancellationTokenSource target)
    {
#if !NET8_0_OR_GREATER
        // Copied from https://github.com/SimonCropp/Polyfill/blob/18243e7e051c347acf023978bd18abd181ea6695/src/Split/netstandard2.1/Polyfill_CancellationTokenSource.cs
        /// <summary>Communicates a request for cancellation asynchronously.</summary>
        public Task CancelAsync()
        {
            if (target.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            var task = Task.Run(target.Cancel);
            while (!target.IsCancellationRequested) ;
            return task;
        }
#endif
    }

#if !NET6_0_OR_GREATER
    // Copied from https://github.com/SimonCropp/Polyfill/blob/18243e7e051c347acf023978bd18abd181ea6695/src/Split/netstandard2.1/Polyfill_Task.cs
    const uint MAX_SUPPORTED_TIMEOUT = 0xfffffffe;

    extension(Task target)
    {
        /// <summary>Gets a <see cref="Task"/> that will complete when this <see cref="Task"/> completes or when the specified <see cref="CancellationToken"/> has cancellation requested.</summary>
        public Task WaitAsync(CancellationToken cancellationToken) =>
            target.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);

        /// <summary>Gets a <see cref="Task"/> that will complete when this <see cref="Task"/> completes or when the specified timeout expires.</summary>
        public Task WaitAsync(TimeSpan timeout) =>
            target.WaitAsync(timeout, default);

        /// <summary>Gets a <see cref="Task"/> that will complete when this <see cref="Task"/> completes, when the specified timeout expires, or when the specified <see cref="CancellationToken"/> has cancellation requested.</summary>
        public async Task WaitAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var milliseconds = (long)timeout.TotalMilliseconds;
            if (milliseconds is < -1 or > MAX_SUPPORTED_TIMEOUT)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            if (target.IsCompleted ||
                (!cancellationToken.CanBeCanceled && timeout == Timeout.InfiniteTimeSpan))
            {
                await target.ConfigureAwait(false);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await Task.FromCanceled(cancellationToken);
            }

            if (timeout == TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            using var cancelSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancelSource.CancelAfter(timeout);
            var cancelTask = new TaskCompletionSource<bool>();
            using (cancelSource.Token.Register(
                       tcs => ((TaskCompletionSource<bool>)tcs!).TrySetResult(true), cancelTask))
            {
                await Task.WhenAny(target, cancelTask.Task).ConfigureAwait(false);
                if (!target.IsCompleted)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await Task.FromCanceled(cancellationToken);
                    }

                    throw new TimeoutException();
                }

                await target.ConfigureAwait(false);
            }
        }
    }

    extension<TResult>(Task<TResult> target)
    {
        /// <summary>
        /// Gets a <see cref="Task"/> that will complete when this <see cref="Task"/> completes, or when the specified <see cref="CancellationToken"/> has cancellation requested.
        /// </summary>
        public Task<TResult> WaitAsync(CancellationToken cancellationToken) =>
            target.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);

        /// <summary>
        /// Gets a <see cref="Task"/> that will complete when this <see cref="Task"/> completes, or when the specified timeout expires.
        /// </summary>
        public Task<TResult> WaitAsync(TimeSpan timeout) =>
            target.WaitAsync(timeout, default);

        /// <summary>
        /// Gets a <see cref="Task"/> that will complete when this <see cref="Task"/> completes, when the specified timeout expires, or when the specified <see cref="CancellationToken"/> has cancellation requested.
        /// </summary>
        public async Task<TResult> WaitAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            await ((Task)target).WaitAsync(timeout, cancellationToken);
            return target.Result;
        }
    }
#endif

    extension(ArgumentNullException)
    {
#if !NET6_0_OR_GREATER
        public static void ThrowIfNull([NotNull] object? argument,
            [CallerArgumentExpression(nameof(argument))]
            string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
#endif

#if !NET7_0_OR_GREATER
        public static void ThrowIfNullOrEmpty([NotNull] string? argument,
            [CallerArgumentExpression(nameof(argument))]
            string? paramName = null)
        {
            if (string.IsNullOrEmpty(argument))
            {
                throw argument is null
                    ? new ArgumentNullException(paramName)
                    : new ArgumentException("Value cannot be empty.", paramName);
            }
        }
#endif
    }

    extension(ObjectDisposedException)
    {
#if !NET7_0_OR_GREATER
        //Link: https://learn.microsoft.com/en-us/dotnet/api/system.objectdisposedexception.throwif?view=net-11.0##system-objectdisposedexception-throwif(system-boolean-system-object)
        public static void ThrowIf(bool condition, object instance)
        {
            if (condition)
            {
                throw new ObjectDisposedException(instance.GetType().FullName);
            }
        }

        //Link: https://learn.microsoft.com/en-us/dotnet/api/system.objectdisposedexception.throwif?view=net-11.0##system-objectdisposedexception-throwif(system-boolean-system-type)
        public static void ThrowIf(bool condition, Type type)
        {
            if (condition)
            {
                throw new ObjectDisposedException(type.FullName);
            }
        }
#endif
    }

    // csharpier-ignore-end
}
