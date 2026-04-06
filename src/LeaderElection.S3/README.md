# LeaderElection.S3

An S3-backed implementation of the distributed leader election pattern for .NET. This implementation
uses S3 conditional PUT operations (ETags) to ensure safe, atomic leadership acquisition and
renewal.

## Features

- **Async/Await Support**: Full async/await pattern support for better performance and scalability
- **Comprehensive Error Handling**: Robust error handling with retry logic and exponential backoff
- **Structured Logging**: Integration with Microsoft.Extensions.Logging for observability
- **Configuration Options**: Flexible configuration through options pattern
- **Event-Driven**: Leadership change and error events for reactive programming
- **Graceful Shutdown**: Proper cleanup and resource disposal
- **Thread Safety**: Thread-safe operations with proper synchronization

## Quick Start

### 1. Install the Package

```bash
dotnet add package LeaderElection
dotnet add package LeaderElection.S3
```

### 2. Configure Services

```csharp
using LeaderElection.S3;
using Minio; // S3 compatible client

var builder = WebApplication.CreateBuilder(args);

// 1. Register your MinIO S3 client
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    return new MinioClient()
        .WithEndpoint("localhost:9000")
        .WithCredentials("accessKey", "secretKey")
        .Build();
});

// 2. Add S3 leader election
builder.Services.AddS3LeaderElection(options =>
{
    options.BucketName = "my-app-locks";
    options.ObjectKey = "leader-lock.json";
    options.LeaseDuration = TimeSpan.FromSeconds(30);
    options.RenewInterval = TimeSpan.FromSeconds(10);
});
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
            // Only runs if this instance currently holds the S3 lease
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

## Configuration (S3Settings)

| Property                 | Default                   | Description                                     |
| :----------------------- | :------------------------ | :---------------------------------------------- |
| `BucketName`             | `leader-election`         | The S3 bucket to store the lease file.          |
| `ObjectKey`              | `(required)`              | The path/name of the lease JSON file.           |
| `InstanceId`             | `Environment.MachineName` | Unique ID for this node.                        |
| `LeaseDuration`          | `30s`                     | How long the lock is valid without a heartbeat. |
| `RenewInterval`          | `10s`                     | How often the leader sends a heartbeat.         |
| `EnableGracefulShutdown` | `true`                    | If true, expires the lease immediately on stop. |

## S3 Specifics

This implementation writes a small JSON file to your bucket. To ensure safety:

1. It uses `If-None-Match: *` when creating a new lock.
2. It uses `If-Match: [etag]` when renewing or taking over an expired lock.

This ensures that even if two instances try to become the leader at the exact same millisecond, S3
will only allow one of them to succeed based on the ETag mismatch protection.

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
