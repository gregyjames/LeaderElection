namespace LeaderElection;

public class LeaderElectionSettingsBase
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxRetryAttempts { get; set; } = 3;
    public bool EnableGracefulShutdown { get; set; } = true;
}