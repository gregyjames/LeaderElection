namespace LeaderElection.BlobStorage;

public class BlobStorageSettings: LeaderElectionSettingsBase
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "leader-election";
    public string BlobName { get; set; } = "leader-election-lock";
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public bool CreateContainerIfNotExists { get; set; } = true;
}