using System.ComponentModel.DataAnnotations;

namespace LeaderElection.BlobStorage;

public class BlobStorageSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// The connection string for the Azure Blob Storage account.
    /// <para/>
    /// Ignored when using the constructor that accepts a BlobContainerClient.
    /// </summary>
    [Required(AllowEmptyStrings = true)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The name of the blob container to use for leader election.
    /// <para/>
    /// Must be a valid Azure Blob Storage container name.
    /// Default is "leader-election-lock".
    /// <para/>
    /// Ignored when using the constructor that accepts a BlobContainerClient.
    /// </summary>
    [Required]
    public string ContainerName { get; set; } = "leader-election";

    /// <summary>
    /// The name of the blob to use for leader election.
    /// <para/>
    /// Must be a valid Azure Blob Storage blob name.
    /// Default is "leader-election-lock".
    /// </summary>
    [Required]
    public string BlobName { get; set; } = "leader-election-lock";

    /// <summary>
    /// The duration of the lease when acquiring leadership.
    /// <para/>
    /// Must be between 15 and 60 seconds, or -1 for infinite.
    /// Default is 30 seconds.
    /// </summary>
    [CustomValidation(
        typeof(BlobStorageSettingsValidator),
        nameof(BlobStorageSettingsValidator.IsValidLeaseDuration)
    )]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to create the blob container if it does not exist.
    /// <para/>
    /// If false, the container must already exist.
    /// Default is true.
    /// </summary>
    public bool CreateContainerIfNotExists { get; set; } = true;
}
