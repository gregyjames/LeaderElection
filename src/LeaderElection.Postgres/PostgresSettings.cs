using System.ComponentModel.DataAnnotations;

namespace LeaderElection.Postgres;

public class PostgresSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// Connection string for the PostgreSQL database.
    /// </summary>
    [Required]
#if NET7_0_OR_GREATER
    public required string ConnectionString { get; set; }
#else
    public string ConnectionString { get; set; } = null!;
#endif

    /// <summary>
    /// The 64-bit advisory lock key to use for leader election.
    /// </summary>
    public long LockId { get; set; }
}
