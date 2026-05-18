namespace LeaderElection.Postgres;

using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

public sealed partial class PostgresLeaderElection : LeaderElectionBase<PostgresSettings>
{
    private readonly NpgsqlDataSource _dataSource;
    private NpgsqlConnection? _connection;

    public PostgresLeaderElection(
        PostgresSettings options,
        NpgsqlDataSource dataSource,
        ILogger<PostgresLeaderElection>? logger = null,
        TimeProvider? timeProvider = null
    )
        : base(options ?? throw new ArgumentNullException(nameof(options)), logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateDataSource(dataSource);
        _dataSource = dataSource;
    }

    // Validate that the provided DataSource is configured in a way that is compatible with
    // advisory locks and won't lead to unexpected behavior.
    private void ValidateDataSource(NpgsqlDataSource dataSource)
    {
        // Multiplexing is not allowed since each instance requires a dedicated connection
        // (required for advisory locks)
        var b = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        if (b.Multiplexing)
        {
            throw new InvalidOperationException(
                "Multiplexing must be disabled for leader election. Remove 'Multiplexing=true' from your connection string or provide a DataSource with multiplexing disabled."
            );
        }

        // In multi-host configurations, Npgsql will automatically route to a standby if the primary
        // is unavailable, resulting in unpredictable/orphaned locks. To avoid this, require an
        // explicit TargetSessionAttributes in multi-host scenarios.
        // Note that this assumes a Single-Primary topology, which is the only topology where
        // advisory locks can be reliably used.
        if (
            b.Host?.Contains(',', StringComparison.OrdinalIgnoreCase) == true
            && !"read-write".Equals(b.TargetSessionAttributes, StringComparison.OrdinalIgnoreCase)
            && !"primary".Equals(b.TargetSessionAttributes, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                "TargetSessionAttributes must be 'read-write' or 'primary' for multi-host leader election. Add 'TargetSessionAttributes=read-write' or 'TargetSessionAttributes=primary' to your connection string or provide a DataSource with the correct settings."
            );
        }

        // Warn if CommandTimeout is not set to a reasonable value, as a long CommandTimeout can
        // lead to long waits if the database becomes unresponsive.
        if (
            b.CommandTimeout <= 0 /*infinity*/
            || b.CommandTimeout > 5 /*seconds*/
        )
        {
            LogUseRecommendedCommandTimeout(b.CommandTimeout);
        }
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_connection != null)
        {
            LogLockAlreadyAcquired(_settings.LockId);
            return false;
        }

        NpgsqlConnection? connection = null;
        var success = false;
        try
        {
            connection = await _dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            using var cmd = CreateCommand(
                connection,
                "SELECT pg_try_advisory_lock(@LockId);",
                "LockId",
                _settings.LockId
            );

            success =
                (await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as bool?)
                ?? false;

            if (success)
            {
                _connection = connection;
                connection = null; // ownership transferred, don't dispose in finally
                LogAcquiredLock(_settings.LockId);
            }
            else
            {
                LogFailureAcquiringLock(
                    LogLevel.Debug,
                    null,
                    _settings.LockId,
                    "Lock is already held by another instance."
                );
            }
        }
        catch (PostgresException ex)
        {
            LogFailureAcquiringLock(
                LogLevel.Information,
                ex,
                _settings.LockId,
                ex.SqlState + ": " + ex.MessageText
            );
        }
        catch (NpgsqlException ex)
        {
            LogFailureAcquiringLock(LogLevel.Warning, ex, _settings.LockId, ex.Message);
        }
        finally
        {
            if (connection != null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        return success;
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_connection == null)
        {
            LogNoActiveLockToRenew();
            return false;
        }

        var success = false;
        try
        {
            // The advisory lock is held as long as the connection is open; this is just a heartbeat.
            using var cmd = CreateCommand(_connection, "SELECT 1;");
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            success = true;
            LogRenewedLock(_settings.LockId);
        }
        catch (PostgresException ex)
        {
            // this is unexpected since we should have the lock.
            // Log as a warning and give up our leadership.
            LogFailureRenewingLock(
                LogLevel.Warning,
                ex,
                _settings.LockId,
                ex.SqlState + ": " + ex.MessageText
            );
        }
        catch (NpgsqlException ex)
        {
            LogFailureRenewingLock(LogLevel.Error, ex, _settings.LockId, ex.Message);
        }
        finally
        {
            if (!success)
            {
                // assume we lost the lock. Abandon it to clean up our state
                await ResetLeadershipAsync().ConfigureAwait(false);
            }
        }

        return success;
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (_connection == null)
        {
            LogNoActiveLockToRelease();
            return;
        }

        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                using var cmd = CreateCommand(
                    _connection,
                    "SELECT pg_advisory_unlock(@LockId);",
                    "LockId",
                    _settings.LockId
                );
                await cmd.ExecuteNonQueryAsync(default).ConfigureAwait(false);
            }

            LogReleasedLock(_settings.LockId);
        }
        catch (PostgresException ex)
        {
            // this is unexpected since we should have the lock.
            // Log as a warning and give up our leadership.
            LogFailureReleasingLock(
                LogLevel.Warning,
                ex,
                _settings.LockId,
                ex.SqlState + ": " + ex.MessageText
            );
        }
        catch (NpgsqlException ex)
        {
            LogFailureReleasingLock(LogLevel.Error, ex, _settings.LockId, ex.Message);
        }
        finally
        {
            // always assume we lost the lock, even if there was an error
            // releasing it in Postgres.
            await ResetLeadershipAsync().ConfigureAwait(false);
        }
    }

    protected override async ValueTask ResetLeadershipAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection != null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await ResetLeadershipAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        /* language=sql */string query,
        string? argName = null,
        object? argValue = null
    )
    {
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
        var cmd = new NpgsqlCommand(query, connection)
        {
            // CommandTimeout = 3, // force a short timeout to avoid hanging
        };
#pragma warning restore CA2100
        if (argName != null)
        {
            cmd.Parameters.AddWithValue(argName, argValue ?? DBNull.Value);
        }
        return cmd;
    }

    [LoggerMessage(LogLevel.Information, "Lock already acquired: {lockId}.")]
    partial void LogLockAlreadyAcquired(long lockId);

    [LoggerMessage(LogLevel.Debug, "Acquired lock {lockId}.")]
    partial void LogAcquiredLock(long lockId);

    [LoggerMessage("Failure acquiring lock {lockId}: {Reason}.")]
    partial void LogFailureAcquiringLock(
        LogLevel logLevel,
        Exception? exception,
        long lockId,
        string reason
    );

    [LoggerMessage(LogLevel.Information, "No active lock to renew.")]
    partial void LogNoActiveLockToRenew();

    [LoggerMessage(LogLevel.Debug, "Renewed lock {lockId}.")]
    partial void LogRenewedLock(long lockId);

    [LoggerMessage("Failure renewing lock {lockId}: {Reason}.")]
    partial void LogFailureRenewingLock(
        LogLevel logLevel,
        Exception? exception,
        long lockId,
        string reason
    );

    [LoggerMessage(LogLevel.Information, "No active lock to release.")]
    partial void LogNoActiveLockToRelease();

    [LoggerMessage(LogLevel.Debug, "Released lock {lockId}.")]
    partial void LogReleasedLock(long lockId);

    [LoggerMessage("Failure releasing lock {lockId}: {Reason}.")]
    partial void LogFailureReleasingLock(
        LogLevel logLevel,
        Exception? exception,
        long lockId,
        string reason
    );

    [LoggerMessage(
        LogLevel.Warning,
        "CommandTimeout of {commandTimeout} seconds may be too high for reliable leader election. Consider setting 'CommandTimeout' to 3-5 seconds."
    )]
    partial void LogUseRecommendedCommandTimeout(int commandTimeout);
}
