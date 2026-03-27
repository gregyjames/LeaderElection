namespace LeaderElection.Postgres;

using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class PostgresLeaderElection : LeaderElectionBase<PostgresSettings>
{
    private NpgsqlConnection? _activeConnection;

    public PostgresLeaderElection(
        IOptions<PostgresSettings> options,
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
                await _activeConnection.DisposeAsync();
                _activeConnection = null;
            }

            var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@LockId);", connection);
            cmd.Parameters.AddWithValue("LockId", settings.LockId);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result is true)
            {
                _activeConnection = connection;
                return true;
            }

            await connection.DisposeAsync();
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring PostgreSQL leadership");
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
            await using var cmd = new NpgsqlCommand("SELECT 1;", _activeConnection);
            await cmd.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error renewing PostgreSQL leadership; connection lost");

            if (_activeConnection != null)
            {
                await _activeConnection.DisposeAsync();
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
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@LockId);", _activeConnection);
                cmd.Parameters.AddWithValue("LockId", settings.LockId);
                await cmd.ExecuteScalarAsync(default);
            }

            logger.LogInformation("Leadership released for instance {InstanceId}", settings.InstanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error releasing PostgreSQL leadership");
        }
        finally
        {
            await _activeConnection.DisposeAsync();
            _activeConnection = null;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await base.DisposeAsyncCore();

        if (_activeConnection != null)
        {
            await _activeConnection.DisposeAsync();
            _activeConnection = null;
        }
    }
}