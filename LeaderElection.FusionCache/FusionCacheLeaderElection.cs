using Microsoft.Extensions.Logging;

namespace LeaderElection.FusionCache;

public class FusionCacheLeaderElection(ILogger<FusionCacheLeaderElection> logger, ZiggyCreatures.Caching.Fusion.FusionCache fusionCache): ILeaderElection
{
    public async ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public bool IsLeader { get; }
    public DateTime LastLeadershipRenewal { get; }
    public event EventHandler<bool>? LeadershipChanged;
    public event EventHandler<Exception>? ErrorOccurred;
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task RunTaskIfLeaderAsync(Func<Task> task, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task RunTaskIfLeaderAsync(Action task, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}