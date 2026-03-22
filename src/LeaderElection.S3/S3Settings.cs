using System.ComponentModel.DataAnnotations;

namespace LeaderElection.S3;

public class S3Settings: LeaderElectionSettingsBase
{
    [Required(AllowEmptyStrings = false)]
    public string BucketName { get; set; } = "leader-election";
    [Required(AllowEmptyStrings = false)]
    public string ObjectKey { get; set; } = string.Empty;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
}