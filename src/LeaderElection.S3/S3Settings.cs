using System.ComponentModel.DataAnnotations;
using Minio;

namespace LeaderElection.S3;

/// <summary>
/// Settings for S3-based leader election.
/// </summary>
public class S3Settings : LeaderElectionSettingsBase
{
    /// <summary>
    /// An optional factory function used to obtain an <see cref="IMinioClient"/>.
    /// </summary>
    /// <remarks>
    /// If not provided, it will attempt to obtain a <see cref="IMinioClient"/> from DI
    /// (assuming the leader election is created via DI).
    /// </remarks>
    public Func<S3Settings, IMinioClient>? MinioClientFactory { get; set; }

    /// <summary>
    /// The name of the S3 bucket to use for leader election. This bucket must exist.
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
    /// Default value is 30 seconds.
    /// </summary>
    [CustomValidation(
        typeof(S3SettingsValidator),
        nameof(S3SettingsValidator.ValidateLeaseDuration)
    )]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Copies the S3 settings from the source to the destination.
    /// </summary>
    public static void Copy(S3Settings src, S3Settings dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        LeaderElectionSettingsBase.Copy(src, dst);
        dst.MinioClientFactory = src.MinioClientFactory;
        dst.BucketName = src.BucketName;
        dst.ObjectKey = src.ObjectKey;
        dst.LeaseDuration = src.LeaseDuration;
    }
}
