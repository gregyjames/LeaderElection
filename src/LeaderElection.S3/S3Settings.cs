namespace LeaderElection.S3;

public class S3Settings: LeaderElectionSettings
{
    public string BucketName { get; set; } = "leader-election";
    public string ObjectKey { get; set; } = string.Empty;
}