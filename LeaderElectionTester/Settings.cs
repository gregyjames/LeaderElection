using LeaderElection.Redis;

namespace LeaderElectionTester;

public class Settings
{
    public RedisSettings Redis { get; set; } = new();
}