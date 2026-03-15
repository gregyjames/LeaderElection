namespace LeaderElection.DistributedCache;
public class DistributedCacheSettings: LeaderElectionSettings
{
    public string LockKey { get; set; } = "leader-election-lock";
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);
}