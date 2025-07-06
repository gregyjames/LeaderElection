using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LeaderElection.Tests;

public abstract class TestBase : IAsyncDisposable
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly ILoggerFactory LoggerFactory;
    protected readonly CancellationTokenSource CancellationTokenSource;

    protected TestBase()
    {
        var services = new ServiceCollection();
        
        // Add logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        LoggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ServiceProvider = services.BuildServiceProvider();
        CancellationTokenSource = new CancellationTokenSource();
    }

    public virtual async ValueTask DisposeAsync()
    {
        CancellationTokenSource.Cancel();
        CancellationTokenSource.Dispose();
        
        if (ServiceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            //ServiceProvider?.Dispose();
        }
    }

    protected async Task WaitForLeadershipChange(ILeaderElection leaderElection, bool expectedLeadership, TimeSpan timeout = default)
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
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, CancellationTokenSource.Token);
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
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, CancellationTokenSource.Token);
        linkedCts.Token.Register(() => tcs.TrySetCanceled());
        
        await tcs.Task;
    }
} 