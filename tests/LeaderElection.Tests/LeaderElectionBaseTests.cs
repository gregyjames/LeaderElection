using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeaderElection.Tests;

[Collection("LeaderElectionBase")]
[Trait("Kind", "Integration")]
[Trait("Category", "Base")]
public class LeaderElectionBaseTests
{
    private sealed class MockLeaderElection(LeaderElectionSettingsBase settings, ILogger logger)
        : LeaderElectionBase<LeaderElectionSettingsBase>(settings, logger), IDisposable
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
            return await TryAcquireResult!(cancellationToken).ConfigureAwait(false);
        }

        protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
        {
            RenewCalls++;
            return await RenewResult!(cancellationToken).ConfigureAwait(false);
        }

        protected override Task ReleaseLeadershipAsync()
        {
            ReleaseCalls++;
            return ReleaseAction?.Invoke() ?? Task.CompletedTask;
        }

        protected override TimeSpan GetNextDelay(int retryCount) => TimeSpan.FromMilliseconds(50);

        private static void Dispose(bool disposing)
        {
            if (disposing)
            {
                // TODO release managed resources here
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    [Fact]
    public async Task StartAsyncShouldAcquireLeadershipSuccessfully()
    {
        var settings = new LeaderElectionSettingsBase
        {
            RenewInterval = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromMilliseconds(50)
        };
        using var sut = new MockLeaderElection(settings, NullLogger.Instance);
        sut.TryAcquireResult = _ => Task.FromResult(true);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        var isLeader = await WaitUntilLeader(sut, true, TimeSpan.FromSeconds(5));

        isLeader.Should().BeTrue();
        sut.TryAcquireCalls.Should().BeGreaterThan(0);

        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsyncShouldRetryAcquisitionWhenFails()
    {
        var settings = new LeaderElectionSettingsBase
        {
            RenewInterval = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            MaxRetryAttempts = 1
        };
        using var sut = new MockLeaderElection(settings, NullLogger.Instance);
        var calls = 0;
        sut.TryAcquireResult = _ => Task.FromResult(++calls > 1);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        var isLeader = await WaitUntilLeader(sut, true, TimeSpan.FromSeconds(5));

        sut.TryAcquireCalls.Should().BeGreaterThan(1);
        isLeader.Should().BeTrue();

        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RenewShouldBeCalledRepeatedlyWhenLeader()
    {
        var settings = new LeaderElectionSettingsBase
        {
            RenewInterval = TimeSpan.FromMilliseconds(20),
            RetryInterval = TimeSpan.FromMilliseconds(20)
        };
        using var sut = new MockLeaderElection(settings, NullLogger.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        // Wait for multiple renewals
        await Task.Delay(500, TestContext.Current.CancellationToken);

        sut.IsLeader.Should().BeTrue();
        sut.RenewCalls.Should().BeGreaterThan(1);

        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LeadershipShouldBeLostWhenRenewalFails()
    {
        var settings = new LeaderElectionSettingsBase
        {
            RenewInterval = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            MaxRetryAttempts = 1
        };
        using var sut = new MockLeaderElection(settings, NullLogger.Instance);
        var tcs = new TaskCompletionSource<bool>();
        sut.LeadershipChanged += (_, leaderShipChanged) => { if (!leaderShipChanged.LeadershipChanged) tcs.TrySetResult(true); };

        sut.RenewResult = _ => Task.FromResult(false);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        var eventFired = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        eventFired.Should().BeTrue();
        sut.IsLeader.Should().BeFalse();

        await sut.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunTaskIfLeaderAsyncShouldOnlyRunWhenLeader()
    {
        var settings = new LeaderElectionSettingsBase();
        using var sut = new MockLeaderElection(settings, NullLogger.Instance);
        var taskExecuted = false;

        await sut.RunTaskIfLeaderAsync(() => taskExecuted = true, TestContext.Current.CancellationToken);
        taskExecuted.Should().BeFalse();

        await sut.TryAcquireLeadershipAsync(TestContext.Current.CancellationToken);
        await sut.RunTaskIfLeaderAsync(() => taskExecuted = true, TestContext.Current.CancellationToken);
        taskExecuted.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsyncShouldReleaseLeadershipWhenEnabled()
    {
        var settings = new LeaderElectionSettingsBase { EnableGracefulShutdown = true };
        using var sut = new MockLeaderElection(settings, NullLogger.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilLeader(sut, true, TimeSpan.FromSeconds(5));

        await sut.StopAsync(TestContext.Current.CancellationToken);

        sut.ReleaseCalls.Should().Be(1);
        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsyncShouldStopCorrectly()
    {
        var settings = new LeaderElectionSettingsBase();
        var sut = new MockLeaderElection(settings, NullLogger.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilLeader(sut, true, TimeSpan.FromSeconds(5));

        await sut.DisposeAsync();

        sut.IsLeader.Should().BeFalse();
    }

    private static async Task<bool> WaitUntilLeader(ILeaderElection leaderElection, bool expected, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (leaderElection.IsLeader == expected)
                    return expected;
                await Task.Delay(20, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }

        return leaderElection.IsLeader;
    }
}
