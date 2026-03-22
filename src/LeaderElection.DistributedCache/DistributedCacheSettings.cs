namespace LeaderElection.DistributedCache;
public class DistributedCacheSettings: LeaderElectionSettingsBase
{
    public string LockKey { get; set; } = "leader-election-lock";
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);
}