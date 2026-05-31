using Microsoft.Extensions.Time.Testing;

namespace LeaderElection.Tests;

internal sealed class FakeLeaderElectionSettings : LeaderElectionSettingsBase
{
    public Func<bool> AcquireResult { get; set; } = () => true;
    public Func<bool> RenewResult { get; set; } = () => true;
    public Action? ReleaseAction { get; set; }

    public int TryAcquireCount { get; set; }
    public int TryRenewCount { get; set; }
    public int TryReleaseCount { get; set; }
    public int LeadershipChangedCount { get; set; }
    public int ErrorCount { get; set; }
}

internal sealed class FakeLeaderElection : LeaderElectionBase<FakeLeaderElectionSettings>
{
    public FakeLeaderElectionSettings Settings => _settings;

    public FakeLeaderElection(FakeLeaderElectionSettings settings, FakeTimeProvider timeProvider)
        : base(settings, timeProvider: timeProvider)
    {
        LeadershipChanged += (s, e) =>
        {
            s.Should().BeSameAs(this);
            e.Should().NotBeNull();
            _settings.LeadershipChangedCount++;
        };

        ErrorOccurred += (s, e) =>
        {
            s.Should().BeSameAs(this);
            e.Should().NotBeNull();
            e.LeadershipException.Should().NotBeNull();
            _settings.ErrorCount++;
        };
    }

    // Use a fixed retry for easier testing
    protected override TimeSpan GetNextDelay(int retryCount) => _settings.RetryInterval;

    protected override Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        _settings.TryAcquireCount++;
        return Task.FromResult(_settings.AcquireResult());
    }

    protected override Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        _settings.TryRenewCount++;
        return Task.FromResult(_settings.RenewResult());
    }

    protected override Task ReleaseLeadershipAsync()
    {
        _settings.TryReleaseCount++;
        _settings.ReleaseAction?.Invoke();
        return Task.CompletedTask;
    }

    protected override ValueTask ResetLeadershipAsync() => ValueTask.CompletedTask;
}
