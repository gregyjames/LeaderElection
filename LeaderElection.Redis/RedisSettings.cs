namespace LeaderElection.Redis;

public class RedisSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6379;
    public string Password { get; set; } = string.Empty;
    public int Database { get; set; } = 0;
    public string LockKey { get; set; } = "leader-election-lock";
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxRetryAttempts { get; set; } = 3;
    public bool EnableGracefulShutdown { get; set; } = true;
}