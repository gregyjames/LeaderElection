using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeaderElection.Tests;

[Collection("LeaderElectionBase")]
[Trait("Kind", "Integration")]
[Trait("Category", "Base")]
public class LeaderElectionBaseTests
{
    private class MockLeaderElection(LeaderElectionSettingsBase settings, ILogger logger) 
        : LeaderElectionBase<LeaderElectionSettingsBase>(settings, logger)
    {
        public int TryAcquireCalls { get; private set; }
        public int RenewCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Func<CancellationToken, Task<bool>>? TryAcquireResult { get; set; } = _ => Task.FromResult(true);
        public Func<CancellationToken, Task<bool>>? RenewResult { get; set; } = _ => Task.FromResult(true);
        public Func<Task>? ReleaseAction { get; set; }

        protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
        {
            TryAcquireCalls++;
            return await TryAcquireResult!(cancellationToken);
        }

        protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
        {
            RenewCalls++;
            return await RenewResult!(cancellationToken);
        }

        protected override Task ReleaseLeadershipAsync()
        {
            ReleaseCalls++;
            return ReleaseAction?.Invoke() ?? Task.CompletedTask;
        }

        protected override TimeSpan GetNextDelay(int retryCount) => TimeSpan.FromMilliseconds(50);
    }

    [Fact]
    public async Task StartAsync_Should_Acquire_Leadership_Successfully()
    {
        var settings = new LeaderElectionSettingsBase 
        { 
            RenewInterval = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromMilliseconds(50)
        };
        var sut = new MockLeaderElection(settings, NullLogger.Instance);
        sut.TryAcquireResult = _ => Task.FromResult(true);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        var isLeader = await WaitUntilLeader(sut, true, TimeSpan.FromSeconds(5));

        isLeader.Should().BeTrue();
        sut.TryAcquireCalls.Should().BeGreaterThan(0);
        
        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_Should_Retry_Acquisition_When_Fails()
    {
        var settings = new LeaderElectionSettingsBase 
        { 
            RenewInterval = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            MaxRetryAttempts = 1
        };
        var sut = new MockLeaderElection(settings, NullLogger.Instance);
        var calls = 0;
        sut.TryAcquireResult = _ => Task.FromResult(++calls > 1);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        var isLeader = await WaitUntilLeader(sut, true, TimeSpan.FromSeconds(5));

        sut.TryAcquireCalls.Should().BeGreaterThan(1);
        isLeader.Should().BeTrue();
        
        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Renew_Should_Be_Called_Repeatedly_When_Leader()
    {
        var settings = new LeaderElectionSettingsBase 
        { 
            RenewInterval = TimeSpan.FromMilliseconds(20),
            RetryInterval = TimeSpan.FromMilliseconds(20)
        };
        var sut = new MockLeaderElection(settings, NullLogger.Instance);
        
        await sut.StartAsync(TestContext.Current.CancellationToken);
        
        // Wait for multiple renewals
        await Task.Delay(500, TestContext.Current.CancellationToken);

        sut.IsLeader.Should().BeTrue();
        sut.RenewCalls.Should().BeGreaterThan(1);
        
        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Leadership_Should_Be_Lost_When_Renewal_Fails()
    {
        var settings = new LeaderElectionSettingsBase 
        { 
            RenewInterval = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            MaxRetryAttempts = 1
        };
        var sut = new MockLeaderElection(settings, NullLogger.Instance);
        var tcs = new TaskCompletionSource<bool>();
        sut.LeadershipChanged += (_, isLeader) => { if (!isLeader) tcs.TrySetResult(true); };
        
        sut.RenewResult = _ => Task.FromResult(false);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        var eventFired = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        eventFired.Should().BeTrue();
        sut.IsLeader.Should().BeFalse();
        
        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunTaskIfLeaderAsync_Should_Only_Run_When_Leader()
    {
        var settings = new LeaderElectionSettingsBase();
        var sut = new MockLeaderElection(settings, NullLogger.Instance);
        var taskExecuted = false;

        await sut.RunTaskIfLeaderAsync(() => taskExecuted = true, TestContext.Current.CancellationToken);
        taskExecuted.Should().BeFalse();

        await sut.TryAcquireLeadershipAsync(TestContext.Current.CancellationToken);
        await sut.RunTaskIfLeaderAsync(() => taskExecuted = true, TestContext.Current.CancellationToken);
        taskExecuted.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_Should_Release_Leadership_When_Enabled()
    {
        var settings = new LeaderElectionSettingsBase { EnableGracefulShutdown = true };
        var sut = new MockLeaderElection(settings, NullLogger.Instance);
        
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilLeader(sut, true, TimeSpan.FromSeconds(5));

        await sut.StopAsync(TestContext.Current.CancellationToken);

        sut.ReleaseCalls.Should().Be(1);
        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_Should_Stop_Correctly()
    {
        var settings = new LeaderElectionSettingsBase();
        var sut = new MockLeaderElection(settings, NullLogger.Instance);
        
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilLeader(sut, true, TimeSpan.FromSeconds(5));

        await sut.DisposeAsync();

        sut.IsLeader.Should().BeFalse();
    }

    private async Task<bool> WaitUntilLeader(ILeaderElection leaderElection, bool expected, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        
        try 
        {
            while (!cts.IsCancellationRequested)
            {
                if (leaderElection.IsLeader == expected)
                    return expected;
                await Task.Delay(20, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        
        return leaderElection.IsLeader;
    }
}
