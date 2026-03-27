using System.ComponentModel.DataAnnotations;

namespace LeaderElection.Postgres;

public class PostgresSettings: LeaderElectionSettingsBase
{
    /// <summary>
    /// Connection string for the PostgreSQL database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
    
    /// <summary>
    /// The 64-bit advisory lock key to use for leader election.
    /// </summary>
    public long LockId { get; set; }
}
