using System.ComponentModel.DataAnnotations;

namespace LeaderElection.S3;

public class S3Settings : LeaderElectionSettingsBase
{
    /// <summary>
    /// The name of the S3 bucket to use for leader election.
    /// This bucket must exist and be accessible with the provided AWS credentials.
    /// <para/>
    /// Default value is "leader-election".
    /// </summary>
    [Required]
    public string BucketName { get; set; } = "leader-election";

    /// <summary>
    /// The key of the S3 object to use for leader election.
    /// </summary>
    [Required]
#if NET7_0_OR_GREATER
    public required string ObjectKey { get; set; }
#else
    public string ObjectKey { get; set; } = null!;
#endif

    /// <summary>
    /// The duration for which a leader holds the leadership before it needs to renew it.
    /// <para/>
    /// Default value is 30 seconds.
    /// </summary>
    [CustomValidation(
        typeof(S3SettingsValidator),
        nameof(S3SettingsValidator.ValidateLeaseDuration)
    )]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
}
