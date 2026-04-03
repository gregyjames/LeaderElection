namespace LeaderElection.Postgres;

using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

public sealed partial class PostgresLeaderElection : LeaderElectionBase<PostgresSettings>
{
    private readonly NpgsqlConnection _connection;
    private bool _ownsLock;

    public PostgresLeaderElection(
        PostgresSettings options,
        NpgsqlConnection connection,
        ILogger<PostgresLeaderElection>? logger = null,
        TimeProvider? timeProvider = null
    )
        : base(options ?? throw new ArgumentNullException(nameof(options)), logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    protected override async Task<bool> TryAcquireLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (_ownsLock)
        {
            LogLockAlreadyAcquired(_settings.LockId);
            return false;
        }

        var success = false;
        try
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@LockId);", _connection);
            cmd.Parameters.AddWithValue("LockId", _settings.LockId);

            var acquired =
                (bool?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? false;

            if (acquired)
            {
                success = true;
                _ownsLock = true;
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
                // assume we lost the lock. Abandon it to clean up our state
                await AbandonLockAsync().ConfigureAwait(false);
            }
        }

        return success;
    }

    protected override async Task<bool> RenewLeadershipInternalAsync(
        CancellationToken cancellationToken
    )
    {
        if (!_ownsLock)
        {
            LogNoActiveLockToRenew();
            return false;
        }

        var success = false;
        try
        {
            // The advisory lock is held as long as the connection is open; this is just a heartbeat.
            using var cmd = new NpgsqlCommand("SELECT 1;", _connection);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

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
                await AbandonLockAsync().ConfigureAwait(false);
            }
        }

        return success;
    }

    protected override async Task ReleaseLeadershipAsync()
    {
        if (!_ownsLock)
        {
            LogNoActiveLockToRelease();
            return;
        }

        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                using var cmd = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(@LockId);",
                    _connection
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
            // always assume we lost the lock, even if there was an error
            // releasing it in Postgres.
            await AbandonLockAsync().ConfigureAwait(false);
        }
    }

    async Task AbandonLockAsync()
    {
        _ownsLock = false;
        await _connection.CloseAsync().ConfigureAwait(false);
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
}
