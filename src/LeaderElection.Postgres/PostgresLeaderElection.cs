namespace LeaderElection.Postgres;

using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

public sealed partial class PostgresLeaderElection : LeaderElectionBase<PostgresSettings>
{
    private NpgsqlConnection? _activeConnection;

    public PostgresLeaderElection(
        PostgresSettings options,
        ILogger<PostgresLeaderElection>? logger = null,
        TimeProvider? timeProvider = null
    )
        : base(options ?? throw new ArgumentNullException(nameof(options)), logger, timeProvider)
    { }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_activeConnection != null)
        {
            LogLockAlreadyAcquired(_settings.LockId);
            return false;
        }

        var success = false;
        var connection = GetConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@LockId);", connection);
            cmd.Parameters.AddWithValue("LockId", _settings.LockId);

            var acquired =
                (bool?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? false;

            if (acquired)
            {
                success = true;
                _activeConnection = connection;
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
            if (!success)
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
        if (_activeConnection == null)
        {
            LogNoActiveLockToRenew();
            return false;
        }

        var success = false;
        try
        {
            // The advisory lock is held as long as the connection is open; this is just a heartbeat.
            using var cmd = new NpgsqlCommand("SELECT 1;", _activeConnection);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            success = true;
            LogRenewedLock(_settings.LockId);
        }
        catch (PostgresException ex)
        {
            LogFailureRenewingLock(
                LogLevel.Information,
                ex,
                _settings.LockId,
                ex.SqlState + ": " + ex.MessageText
            );
        }
        catch (NpgsqlException ex)
        {
            LogFailureRenewingLock(LogLevel.Information, ex, _settings.LockId, ex.Message);
        }
        finally
        {
            if (!success)
            {
                // Dispose the connection to release the lock if we failed to renew it
                await _activeConnection.DisposeAsync().ConfigureAwait(false);
                _activeConnection = null;
            }
        }

        return success;
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (_activeConnection == null)
        {
            LogNoActiveLockToRelease();
            return;
        }

        try
        {
            if (_activeConnection.State == ConnectionState.Open)
            {
                using var cmd = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(@LockId);",
                    _activeConnection
                );
                cmd.Parameters.AddWithValue("LockId", _settings.LockId);
                await cmd.ExecuteScalarAsync(default).ConfigureAwait(false);
            }

            LogReleasedLock(_settings.LockId);
        }
        catch (PostgresException ex)
        {
            LogFailureReleasingLock(
                LogLevel.Information,
                ex,
                _settings.LockId,
                ex.SqlState + ": " + ex.MessageText
            );
        }
        catch (NpgsqlException ex)
        {
            LogFailureReleasingLock(LogLevel.Information, ex, _settings.LockId, ex.Message);
        }
        finally
        {
            // Dispose the connection to ensure the lock is released even if the unlock
            // command failed.
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

    private NpgsqlConnection GetConnection()
    {
        if (_settings.ConnectionFactory != null)
        {
            if (!string.IsNullOrWhiteSpace(_settings.ConnectionString))
            {
                LogIgnoringConnectionStringBecauseFactoryIsSet();
            }

            return _settings.ConnectionFactory(_settings)
                ?? throw new InvalidOperationException("ConnectionFactory returned null.");
        }

        if (!string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            return new NpgsqlConnection(_settings.ConnectionString);
        }

        throw new InvalidOperationException(
            "Either ConnectionFactory must be provided or ConnectionString must be non-empty."
        );
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

    [LoggerMessage(LogLevel.Warning, "Ignoring ConnectionString because ConnectionFactory is set.")]
    partial void LogIgnoringConnectionStringBecauseFactoryIsSet();
}
