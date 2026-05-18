# LeaderElection.Postgres

A PostgreSQL-backed implementation of the distributed leader election pattern for .NET. This
implementation uses extremely fast, native PostgreSQL "Advisory Locks" (`pg_try_advisory_lock`) to
ensure safe, atomic leadership acquisition tied directly to the lifetime of the database connection.

## Features

- **Async/Await Support**: Full async/await pattern support for better performance and scalability
- **Comprehensive Error Handling**: Robust error handling with retry logic and exponential backoff
- **Structured Logging**: Integration with Microsoft.Extensions.Logging for observability
- **Configuration Options**: Flexible configuration through options pattern
- **Event-Driven**: Leadership change and error events for reactive programming
- **Graceful Shutdown**: Proper cleanup and resource disposal
- **Session-Level Locks**: Fully avoids row-level contention and deadlocks by locking in shared
  memory

## Quick Start

### 1. Install the Package

```bash
dotnet add package LeaderElection
dotnet add package LeaderElection.Postgres
```

### 2. Configure Services

```csharp
using LeaderElection.Postgres;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Register a NpgsqlDataSource (from Npgsql.DependencyInjection) specifically for
// Leader Election use...
const string postgresLeaderElectionDataSource = "PostgresLeaderElectionDataSource";
builder.Services.AddNpgsqlDataSource(
    // Use short CommandTimeout to avoid long waits if the database becomes unresponsive.
    "Host=localhost;Database=mydb;Username=myuser;Password=mypass;Timeout=5;CommandTimeout=3;",
    serviceKey: postgresLeaderElectionDataSource,
);

builder.Services.AddPostgresLeaderElection(builder =>
    builder
        .WithRegisteredDataSource(postgresLeaderElectionDataSource)
        .WithSettings(options =>
        {
            options.LockId = 1337; // A unique 64-bit integer representing this specific lock

            // Use a short RenewInterval to quickly detect and recover from failed leaders.
            // Note that the actual detection time will be at least the sum of the CommandTimeout
            // and RenewInterval, so keep CommandTimeout low as well.
            // Alternatively, you can specify a KeepAlive interval in the connection string
            // (e.g., "KeepAlive=5") to have the Npgsql driver detect a broken connection.
            options.RenewInterval = TimeSpan.FromSeconds(5); // aggressive renew
        })
);
```

### 3. Use in Your Service

```csharp
public class Worker : BackgroundService
{
    private readonly ILeaderElection _leaderElection;
    private readonly ILogger<Worker> _logger;

    public Worker(ILeaderElection leaderElection, ILogger<Worker> logger)
    {
        _leaderElection = leaderElection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start the background election loop
        await _leaderElection.StartAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Only runs if this instance currently holds the Postgres advisory lock
            await _leaderElection.RunTaskIfLeaderAsync(async () =>
            {
                _logger.LogInformation("Instance is leader. Doing work...");
                await Task.Delay(1000, stoppingToken);
            }, stoppingToken);

            await Task.Delay(5000, stoppingToken);
        }
    }
}
```

## Configuration (PostgresSettings)

| Property                 | Default          | Description                                                                                                                              |
| :----------------------- | :--------------- | :--------------------------------------------------------------------------------------------------------------------------------------- |
| `DataSourceFactory`      | `null`           | A factory function used to create an `NpgsqlDataSource`. If not specified, the `ConnectionString` will be tried.                         |
| `ConnectionString`       | `null`           | The connection string for the PostgreSQL database. If not specified, the `NpgsqlDataSource` from the DI container will be used.          |
| `LockId`                 | `0`              | The unique 64-bit advisory lock id to use for leader election.                                                                           |
| `InstanceId`             | `Guid.NewGuid()` | Unique ID for this node.                                                                                                                 |
| `RenewInterval`          | `10s`            | The interval at which the leader will attempt to renew its leadership.                                                                   |
| `RetryInterval`          | `5s`             | The interval at which non-leader instances will attempt to acquire leadership.                                                           |
| `RetryBackoffFactor`     | `2.0`            | The exponential backoff factor applied to the `RetryInterval`.                                                                           |
| `RetryJitter`            | `0.10`           | The jitter percentage to apply to the retry interval to avoid thundering herd when multiple contenders are trying to acquire leadership. |
| `MaxRetryInterval`       | `40s`            | The maximum retry interval (after applying the backoff factor and jitter) when acquiring leadership.                                     |
| `EnableGracefulShutdown` | `true`           | If true, explicitly unlocks via `pg_advisory_unlock` on stop. It is highly recommended to leave this enabled.                            |

## PostgreSQL Specifics

Instead of executing slow `UPDATE` queries on rows with expiration timestamps, this package executes
`SELECT pg_try_advisory_lock(@LockId);` under the hood.

This requests a **session-level lock** from PostgreSQL itself. If the application crashes, the host
node dies, or the network connection drops, PostgreSQL instantly cleans up the connection and drops
the lock. This allows other backup nodes to acquire leadership immediately without having to wait
for traditional polling or lease-expiration timers.

Additionally, to prevent silent network partitions from locking up the system, the leader election
class periodically executes a lightweight `SELECT 1;` to verify the physical connection is still
healthy and the lock holds true.

## PostgreSQL Requirements

- The database is required to implement a **Single-Primary** topology since this Leader Election
  algorithm relies on **advisory locks**, which are not replicated in multi-host configurations.

- In multi-host configurations (e.g., `host=host1,host2,etc`), an explicit
  `TargetSessionAttributes=read-write` (or `primary`) is required in order to ensure all
  leader-election candidates are connected to the same instance. An error will be thrown if the
  connection string contains multiple hosts without the required `TargetSessionAttributes`.

- Multiplexing is not supported. An error will be thrown if the connection string contains
  `Multiplexing=true`.

- The PostgreSQL user must have permissions to execute `pg_try_advisory_lock` and
  `pg_advisory_unlock` functions, which are typically granted by default to all users.

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests
5. Submit a pull request

## License

MIT License

Copyright (c) 2025 Greg James

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Icon

["Business icon pack leader icon"](https://cdn0.iconfinder.com/data/icons/business-1797/32/19-512.png)
by [mr icons](https://www.iconfinder.com/mr-icons-1) is licensed under
[CC BY 4.0](http://creativecommons.org/licenses/by/4.0)
