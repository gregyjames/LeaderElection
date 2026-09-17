using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;
using Xunit;

namespace LeaderElection.FusionCache.Tests;

public class FusionCacheLeaderElectionTests
{
    private static IFusionCache CreateFusionCache() =>
        new FusionCacheBuilder()
            .UseMemoryCache()
            .Build();

    private static FusionCacheSettings CreateSettings(string instanceId, string lockKey = "test-lock") =>
        new()
        {
            InstanceId = instanceId,
            LockKey = lockKey,
            LockExpiry = TimeSpan.FromSeconds(30),
            RenewInterval = TimeSpan.FromSeconds(10),
            RetryInterval = TimeSpan.FromSeconds(1),
            MaxRetryInterval = TimeSpan.FromSeconds(30),
        };

    [Fact]
    public void Constructor_NullSettings_ThrowsArgumentNullException()
    {
        var cache = CreateFusionCache();

        var exception = Assert.Throws<ArgumentNullException>(
            () => new FusionCacheLeaderElection(settings: null, cache)
        );

        Assert.Equal(nameof(FusionCacheSettings), exception.ParamName);
    }

    [Fact]
    public void Constructor_NullCache_ThrowsArgumentNullException()
    {
        var settings = CreateSettings("instance-1");

        var exception = Assert.Throws<ArgumentNullException>(
            () => new FusionCacheLeaderElection(settings, cache: null)
        );

        Assert.Equal(nameof(IFusionCache), exception.ParamName);
    }

    [Fact]
    public async Task TryAcquireLeadership_WhenNoLockExists_ShouldAcquireLeadership()
    {
        var cache = CreateFusionCache();
        var leader = CreateTestableFusionCacheLeaderElection(cache, "instance-1");

        var acquired = await leader.TryAcquireLeadershipAsync(CancellationToken.None);

        Assert.True(acquired);
        Assert.Equal("instance-1", await cache.GetOrDefaultAsync<string>("test-lock"));
    }

    [Fact]
    public async Task TryAcquireLeadership_WhenLockIsOwnedByAnotherInstance_ShouldNotAcquireLeadership()
    {
        var cache = CreateFusionCache();
        var firstLeader = CreateTestableFusionCacheLeaderElection(cache, "instance-1");
        var secondLeader = CreateTestableFusionCacheLeaderElection(cache, "instance-2");

        await firstLeader.TryAcquireLeadershipAsync(CancellationToken.None);
        var secondAcquired = await secondLeader.TryAcquireLeadershipAsync(CancellationToken.None);

        Assert.False(secondAcquired);
        Assert.Equal("instance-1", await cache.GetOrDefaultAsync<string>("test-lock"));
    }

    [Fact]
    public async Task TryAcquireLeadership_WhenLockIsAlreadyOwned_ShouldReturnTrue()
    {
        var cache = CreateFusionCache();
        var leader = CreateTestableFusionCacheLeaderElection(cache, "instance-1");

        await leader.TryAcquireLeadershipAsync(CancellationToken.None);
        var reacquired = await leader.TryAcquireLeadershipAsync(CancellationToken.None);

        Assert.True(reacquired);
    }

    [Fact]
    public async Task RenewLeadership_WhenOwner_ShouldSucceed()
    {
        var cache = CreateFusionCache();
        var leader = CreateTestableFusionCacheLeaderElection(cache, "instance-1");

        await leader.TryAcquireLeadershipAsync(CancellationToken.None);
        var renewed = await leader.RenewLeadershipAsync(CancellationToken.None);

        Assert.True(renewed);
    }

    [Fact]
    public async Task RenewLeadership_WhenNotOwner_ShouldFail()
    {
        var cache = CreateFusionCache();
        var firstLeader = CreateTestableFusionCacheLeaderElection(cache, "instance-1");
        var secondLeader = CreateTestableFusionCacheLeaderElection(cache, "instance-2");

        await firstLeader.TryAcquireLeadershipAsync(CancellationToken.None);
        var renewed = await secondLeader.RenewLeadershipAsync(CancellationToken.None);

        Assert.False(renewed);
    }

    [Fact]
    public async Task ReleaseLeadership_WhenOwner_ShouldAllowAnotherInstanceToAcquire()
    {
        var cache = CreateFusionCache();
        var firstLeader = CreateTestableFusionCacheLeaderElection(cache, "instance-1");
        var secondLeader = CreateTestableFusionCacheLeaderElection(cache, "instance-2");

        await firstLeader.TryAcquireLeadershipAsync(CancellationToken.None);
        await firstLeader.ReleaseLeadershipAsync();

        var secondAcquired = await secondLeader.TryAcquireLeadershipAsync(CancellationToken.None);
        Assert.True(secondAcquired);
    }

    [Fact]
    public async Task ReleaseLeadership_WhenNotOwner_ShouldNotRemoveLock()
    {
        var cache = CreateFusionCache();
        var firstLeader = CreateTestableFusionCacheLeaderElection(cache, "instance-1");
        var secondLeader = CreateTestableFusionCacheLeaderElection(cache, "instance-2");

        await firstLeader.TryAcquireLeadershipAsync(CancellationToken.None);
        await secondLeader.ReleaseLeadershipAsync();

        Assert.Equal("instance-1", await cache.GetOrDefaultAsync<string>("test-lock"));
    }

    private static TestableFusionCacheLeaderElection CreateTestableFusionCacheLeaderElection(
        IFusionCache cache,
        string instanceId
    ) =>
        new(CreateSettings(instanceId), cache);

    private sealed class TestableFusionCacheLeaderElection : FusionCacheLeaderElection
    {
        public TestableFusionCacheLeaderElection(FusionCacheSettings settings, IFusionCache cache)
            : base(settings, cache, logger: null, timeProvider: null)
        {
        }

        public Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken) =>
            TryAcquireLeadershipInternalAsync(cancellationToken);

        public Task<bool> RenewLeadershipAsync(CancellationToken cancellationToken) =>
            RenewLeadershipInternalAsync(cancellationToken);

        public Task ReleaseLeadershipAsync() => ReleaseLeadershipAsyncInternal();

        private Task ReleaseLeadershipAsyncInternal() =>
            base.ReleaseLeadershipAsync();
    }
}
