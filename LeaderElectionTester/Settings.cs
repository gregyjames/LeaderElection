using LeaderElection.Redis;

namespace LeaderElectionTester;

internal sealed class Settings
{
    public RedisSettings Redis { get; init; } = new();
}
