# Leader Election

A C# implementaion of the distributed leader election pattern using common paradigms.

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
dotnet add package LeaderElection.Redis
```

### 2. Configure Services
Look in LeaderElectionTester for a full example.

```csharp
using LeaderElection.BlobStorage;

var builder = WebApplication.CreateBuilder(args);

// Add leader election with configuration
collection.AddBlobStorageLeaderElection(settings =>
{
    // blob test using azurite
    settings.ConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";
    settings.BlobName = "leader_election_tester";
    settings.LeaseDuration = TimeSpan.FromSeconds(30);
    settings.RenewInterval = TimeSpan.FromSeconds(10);
    settings.RetryInterval = TimeSpan.FromSeconds(5);
    settings.MaxRetryAttempts = 3;
    settings.EnableGracefulShutdown = true;
});
```

### 3. Use in Your Service

```csharp
public class MyService : BackgroundService
{
    private readonly ILeaderElection _leaderElection;
    private readonly ILogger<MyService> _logger;

    public MyService(ILeaderElection leaderElection, ILogger<MyService> logger)
    {
        _leaderElection = leaderElection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to events
        _leaderElection.LeadershipChanged += OnLeadershipChanged;
        _leaderElection.ErrorOccurred += OnErrorOccurred;

        try
        {
            // Start leader election
            await _leaderElection.StartAsync(stoppingToken);

            // Run leader tasks
            while (!stoppingToken.IsCancellationRequested)
            {
                await _leaderElection.RunTaskIfLeaderAsync(async () =>
                {
                    _logger.LogInformation("Executing leader task");
                    await DoLeaderWorkAsync();
                }, stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally
        {
            // Cleanup
            _leaderElection.LeadershipChanged -= OnLeadershipChanged;
            _leaderElection.ErrorOccurred -= OnErrorOccurred;
            await _leaderElection.StopAsync(stoppingToken);
        }
    }

    private void OnLeadershipChanged(object? sender, bool isLeader)
    {
        _logger.LogInformation("Leadership changed: {IsLeader}", isLeader);
    }

    private void OnErrorOccurred(object? sender, Exception exception)
    {
        _logger.LogError(exception, "Leader election error");
    }
}
```

## API Reference

### Methods

- `StartAsync(CancellationToken)` - Start the leader election process
- `StopAsync(CancellationToken)` - Stop the leader election process
- `TryAcquireLeadershipAsync(CancellationToken)` - Manually attempt to acquire leadership
- `RunTaskIfLeaderAsync(Func<Task>, CancellationToken)` - Execute a task only if this instance is the leader
- `RunTaskIfLeaderAsync(Action, CancellationToken)` - Execute a synchronous task only if this instance is the leader

### Properties

- `IsLeader` - Returns true if this instance is currently the leader
- `LastLeadershipRenewal` - Date/Time of the last accquisition of leadership

### Events

- `LeadershipChanged` - Fired when leadership status changes
- `ErrorOccurred` - Fired when an error occurs during leader election

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests
5. Submit a pull request

## License

MIT License

Copyright (c) 2025 Greg James

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Icon
["Business icon pack leader icon"](https://cdn0.iconfinder.com/data/icons/business-1797/32/19-512.png) by [mr icons](https://www.iconfinder.com/mr-icons-1) is licensed under [CC BY 4.0](http://creativecommons.org/licenses/by/4.0)