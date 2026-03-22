using System.ComponentModel.DataAnnotations;

namespace LeaderElection.Postgres;

public class PostgresSettings
{
    /// <summary>
    /// Connection string for the PostgreSQL database.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The unique identifier for the instance attempting to acquire leadership.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string InstanceId { get; set; } = Environment.MachineName;

    /// <summary>
    /// The 64-bit advisory lock key to use for leader election.
    /// </summary>
    [Required]
    public long LockId { get; set; }

    /// <summary>
    /// The interval to wait before retrying a failed leadership acquisition.
    /// </summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// If true, the instance will explicitly release leadership upon graceful shutdown.
    /// </summary>
    public bool EnableGracefulShutdown { get; set; } = true;
}
