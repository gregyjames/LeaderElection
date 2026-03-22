namespace LeaderElection.S3;

public class S3Settings: LeaderElectionSettingsBase
{
    public string BucketName { get; set; } = "leader-election";
    public string ObjectKey { get; set; } = string.Empty;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
}