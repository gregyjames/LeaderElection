namespace LeaderElection.Redis;

public class RedisSettings: LeaderElectionSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6379;
    public string Password { get; set; } = string.Empty;
    public int Database { get; set; } = 0;
    public string LockKey { get; set; } = "leader-election-lock";
}