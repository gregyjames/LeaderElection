namespace LeaderElection.DistributedCache;
public class DistributedCacheSettings
{
    public string LockKey { get; set; } = "leader-election-lock";
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxRetryAttempts { get; set; } = 10;
    public bool EnableGracefulShutdown { get; set; } = true;
}