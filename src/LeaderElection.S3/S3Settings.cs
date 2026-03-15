namespace LeaderElection.S3;

public class S3Settings
{
    public string BucketName { get; set; } = "leader-election";
    public string ObjectKey { get; set; } = string.Empty;
    public string InstanceId { get; set; } = Environment.MachineName;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxRetryAttempts { get; set; } = 3;
    public bool EnableGracefulShutdown { get; set; } = true;
}