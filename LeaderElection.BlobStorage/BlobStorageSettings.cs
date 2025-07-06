namespace LeaderElection.BlobStorage;

public class BlobStorageSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "leader-election";
    public string BlobName { get; set; } = "leader-election-lock";
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxRetryAttempts { get; set; } = 3;
    public bool EnableGracefulShutdown { get; set; } = true;
    public bool CreateContainerIfNotExists { get; set; } = true;
} 