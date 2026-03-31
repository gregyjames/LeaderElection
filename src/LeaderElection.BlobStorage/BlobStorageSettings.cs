using System.ComponentModel.DataAnnotations;
using Azure.Storage.Blobs;

namespace LeaderElection.BlobStorage;

public class BlobStorageSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// The connection string for the Azure Blob Storage account.
    /// <para/>
    /// Ignored when <see cref="BlobClientFactory"/> is set.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// An optional factory function to create a <see cref="BlobClient"/> instance for leader election.
    /// <para/>
    /// If set, this factory will be used to get a <see cref="BlobClient"/> instead of using
    /// the <see cref="ConnectionString"/> setting.
    /// <para/>
    /// If neither <see cref="BlobClientFactory"/> nor <see cref="ConnectionString"/>
    /// are set, a <see cref="BlobClientService"/> will be resolved from the DI container.
    /// </summary>
    public Func<
        BlobStorageSettings,
        CancellationToken,
        Task<BlobClient>
    >? BlobClientFactory { get; set; }

    /// <summary>
    /// The name of the blob container to use for leader election.
    /// <para/>
    /// Must be a valid Azure Blob Storage container name.
    /// Default is "leader-election-lock".
    /// <para/>
    /// Ignored when <see cref="BlobClientFactory"/> is set.
    /// </summary>
    [Required]
    public string ContainerName { get; set; } = "leader-election";

    /// <summary>
    /// The name of the blob to use for leader election.
    /// <para/>
    /// Must be a valid Azure Blob Storage blob name.
    /// Default is "leader-election-lock".
    /// <para/>
    /// Ignored when <see cref="BlobClientFactory"/> is set.
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

    /// <summary>
    /// Copies the BlobStorage settings from the source to the destination.
    /// </summary>
    public static void Copy(BlobStorageSettings src, BlobStorageSettings dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        LeaderElectionSettingsBase.Copy(src, dst);
        dst.ConnectionString = src.ConnectionString;
        dst.BlobClientFactory = src.BlobClientFactory;
        dst.ContainerName = src.ContainerName;
        dst.BlobName = src.BlobName;
        dst.LeaseDuration = src.LeaseDuration;
        dst.CreateContainerIfNotExists = src.CreateContainerIfNotExists;
    }
}
