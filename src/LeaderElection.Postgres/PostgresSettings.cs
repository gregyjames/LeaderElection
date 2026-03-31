using Npgsql;

namespace LeaderElection.Postgres;

public class PostgresSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// Factory function to create a new <see cref="NpgsqlConnection"/>.
    /// </summary>
    /// <remarks>
    /// If not provided, a connection will be created using the
    /// <see cref="ConnectionString"/> property, or if that is not set,
    /// it will attempt to obtain a <see cref="NpgsqlConnection"/> from DI
    /// (assuming the leader election is created via DI).
    /// </remarks>
    public Func<PostgresSettings, NpgsqlConnection>? ConnectionFactory { get; set; }

    /// <summary>
    /// Connection string for the <see cref="NpgsqlConnection"/>.
    /// Ignored if <see cref="ConnectionFactory"/> is set.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The 64-bit advisory lock key to use for leader election.
    /// </summary>
    public long LockId { get; set; }

    /// <summary>
    /// Copies the PostgreSQL settings from the source to the destination.
    /// </summary>
    public static void Copy(PostgresSettings src, PostgresSettings dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        LeaderElectionSettingsBase.Copy(src, dst);
        dst.ConnectionString = src.ConnectionString;
        dst.ConnectionFactory = src.ConnectionFactory;
        dst.LockId = src.LockId;
    }
}
