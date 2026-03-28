using System.Diagnostics.CodeAnalysis;

namespace LeaderElection.Postgres;

using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

[SuppressMessage("Design", "CA1031:Do not catch general exception types")]
public sealed partial class PostgresLeaderElection : LeaderElectionBase<PostgresSettings>
{
    private NpgsqlConnection? _activeConnection;

    public PostgresLeaderElection(
        IOptions<PostgresSettings>? options,
        ILogger<PostgresLeaderElection> logger)
        : base(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger)
    {
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_activeConnection != null)
            {
                await _activeConnection.DisposeAsync().ConfigureAwait(false);
                _activeConnection = null;
            }

            var connection = new NpgsqlConnection(Settings.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@LockId);", connection);
            cmd.Parameters.AddWithValue("LockId", Settings.LockId);

            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is true)
            {
                _activeConnection = connection;
                return true;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            LogErrorAcquiringPostgresqlLeadership(Logger, ex);
            return false;
        }
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(CancellationToken cancellationToken)
    {
        if (_activeConnection is not { State: ConnectionState.Open })
        {
            return false;
        }

        try
        {
            // The advisory lock is held as long as the connection is open; this is just a heartbeat.
            using var cmd = new NpgsqlCommand("SELECT 1;", _activeConnection);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogErrorRenewingPostgresqlLeadershipConnectionLost(Logger, ex);

            if (_activeConnection != null)
            {
                await _activeConnection.DisposeAsync().ConfigureAwait(false);
                _activeConnection = null;
            }

            return false;
        }
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (_activeConnection == null)
            return;

        try
        {
            if (_activeConnection.State == ConnectionState.Open)
            {
                using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@LockId);", _activeConnection);
                cmd.Parameters.AddWithValue("LockId", Settings.LockId);
                await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }

            LogLeadershipReleasedForInstanceInstanceId(Logger, Settings.InstanceId);
        }
        catch (Exception ex)
        {
            LogErrorReleasingPostgresqlLeadership(Logger, ex);
        }
        finally
        {
            await _activeConnection.DisposeAsync().ConfigureAwait(false);
            _activeConnection = null;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);

        if (_activeConnection != null)
        {
            await _activeConnection.DisposeAsync().ConfigureAwait(false);
            _activeConnection = null;
        }
    }

    [LoggerMessage(LogLevel.Error, "Error acquiring PostgreSQL leadership")]
    static partial void LogErrorAcquiringPostgresqlLeadership(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "Error renewing PostgreSQL leadership; connection lost")]
    static partial void LogErrorRenewingPostgresqlLeadershipConnectionLost(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Information, "Leadership released for instance {instanceId}")]
    static partial void LogLeadershipReleasedForInstanceInstanceId(ILogger logger, string instanceId);

    [LoggerMessage(LogLevel.Error, "Error releasing PostgreSQL leadership")]
    static partial void LogErrorReleasingPostgresqlLeadership(ILogger logger, Exception exception);
}
