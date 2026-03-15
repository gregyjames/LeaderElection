namespace LeaderElection.BlobStorage;

public class BlobStorageSettings: LeaderElectionSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "leader-election";
    public string BlobName { get; set; } = "leader-election-lock";
    public bool CreateContainerIfNotExists { get; set; } = true;
} 