using Npgsql;

namespace LeaderElection.Postgres;

/// <summary>
/// Settings for PostgreSQL-based leader election.
/// </summary>
public class PostgresSettings : LeaderElectionSettingsBase
{
    /// <summary>
    /// An optional factory function used to obtain an <see cref="NpgsqlConnection"/>.
    /// </summary>
    /// <remarks>
    /// If not provided, a connection will be created using the
    /// <see cref="ConnectionString"/> property. If that is null or empty,
    /// it will attempt to obtain a <see cref="NpgsqlConnection"/> from DI
    /// (assuming the leader election is created via DI).
    /// </remarks>
    public Func<PostgresSettings, NpgsqlConnection>? ConnectionFactory { get; set; }

    /// <summary>
    /// Optional connection string for the <see cref="NpgsqlConnection"/>.
    /// </summary>
    /// <remarks>
    /// Ignored if <see cref="ConnectionFactory"/> is set.
    /// </remarks>
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
        dst.ConnectionFactory = src.ConnectionFactory;
        dst.ConnectionString = src.ConnectionString;
        dst.LockId = src.LockId;
    }
}
