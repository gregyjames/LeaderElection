using System.ComponentModel.DataAnnotations;
using Azure.Storage.Blobs;

namespace LeaderElection.BlobStorage;

/// <summary>
/// Settings for Azure Blob Storage-based leader election.
/// </summary>
public class BlobStorageSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// An optional factory function used to create a <see cref="BlobClient"/> instance.
    /// </summary>
    /// <remarks>
    /// If not provided, a <see cref="BlobServiceClient"/> will be created using the
    /// <see cref="ConnectionString"/> property. If that is null or empty,
    /// it will attempt to obtain a <see cref="BlobServiceClient"/> from DI
    /// (assuming the leader election is created via DI).
    /// <para/>
    /// The returned <see cref="BlobClient"/> should point to the blob specified by the
    /// <see cref="ContainerName"/> and <see cref="BlobName"/> properties, and should
    /// be able to perform lease operations on that blob.
    /// </remarks>
    public Func<
        BlobStorageSettings,
        CancellationToken,
        Task<BlobClient>
    >? BlobClientFactory { get; set; }

    /// <summary>
    /// Optional connection string for the Azure Storage account.
    /// </summary>
    /// <remarks>
    /// Ignored if <see cref="BlobClientFactory"/> is set.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The name of the blob container to use for leader election.
    /// Default is "leader-election-lock".
    /// </summary>
    /// <remarks>
    /// Will be created if it does not exist and <see cref="CreateContainerIfNotExists"/> is true.
    /// <para/>
    /// Must be a valid Azure Blob Storage container name. See
    /// https://learn.microsoft.com/en-us/rest/api/storageservices/blob-service-concepts.
    /// </remarks>
    [Required]
    public string ContainerName { get; set; } = "leader-election";

    /// <summary>
    /// The name of the blob to use for leader election.
    /// </summary>
    /// <remarks>
    /// Must be a valid Azure Blob Storage blob name. See
    /// https://learn.microsoft.com/en-us/rest/api/storageservices/blob-service-concepts.
    /// <para/>
    /// This blob will be created if it does not exist. Otherwise the blob will not be modified.
    /// <para/>
    /// Minimum required RBAC role is <pre>Storage Blob Data Contributor</pre> on the
    /// container or blob level.
    /// <para/>
    /// Default is "leader-election-lock".
    /// </remarks>
    [Required]
    public string BlobName { get; set; } = "leader-election-lock";

    /// <summary>
    /// The duration of the lease when acquiring leadership.
    /// </summary>
    /// <remarks>
    /// Must be between 15 and 60 seconds, or -1 for infinite.
    /// <para/>
    /// Default is 30 seconds.
    /// </remarks>
    [CustomValidation(
        typeof(BlobStorageSettingsValidator),
        nameof(BlobStorageSettingsValidator.IsValidLeaseDuration)
    )]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to create the blob container if it does not exist.
    /// Default is true.
    /// </summary>
    /// <remarks>
    /// If false, the container must already exist.
    /// </remarks>
    public bool CreateContainerIfNotExists { get; set; } = true;

    /// <summary>
    /// Copies the BlobStorage settings from the source to the destination.
    /// </summary>
    public static void Copy(BlobStorageSettings src, BlobStorageSettings dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        LeaderElectionSettingsBase.Copy(src, dst);
        dst.BlobClientFactory = src.BlobClientFactory;
        dst.ConnectionString = src.ConnectionString;
        dst.ContainerName = src.ContainerName;
        dst.BlobName = src.BlobName;
        dst.LeaseDuration = src.LeaseDuration;
        dst.CreateContainerIfNotExists = src.CreateContainerIfNotExists;
    }
}
