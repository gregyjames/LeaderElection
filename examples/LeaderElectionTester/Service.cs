using LeaderElection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LeaderElectionTester;

internal sealed class Service : BackgroundService
{
    private readonly ILogger<Service> _logger;
    private readonly ILeaderElection _election;

    public Service(ILogger<Service> logger, ILeaderElection election)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(election);
        _logger = logger;
        _election = election;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Service is running.");

        // Subscribe to leadership change events
        _election.LeadershipChanged += OnLeadershipChanged;
        _election.ErrorOccurred += OnErrorOccurred;

        try
        {
            // Start the leader election
            await _election.StartAsync(stoppingToken).ConfigureAwait(false);

            // Keep the service running and periodically check leadership status
            while (!stoppingToken.IsCancellationRequested)
            {
                await _election
                    .RunTaskIfLeaderAsync(
                        () =>
                        {
                            _logger.LogInformation(
                                "Executing leader task at {Time}",
                                DateTime.UtcNow
                            );
                        },
                        stoppingToken
                    )
                    .ConfigureAwait(false);

                _logger.LogInformation("Doing something else...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Unsubscribe from events
            _election.LeadershipChanged -= OnLeadershipChanged;
            _election.ErrorOccurred -= OnErrorOccurred;

            // Stop the leader election
            await _election.StopAsync(stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("Service has stopped.");
        }
    }

    private void OnErrorOccurred(object? sender, LeaderElectionErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "Error occurred in leader election");
    }

    private void OnLeadershipChanged(object? sender, LeadershipChangedEventArgs e)
    {
        _logger.LogInformation(e.IsLeader
            ? "This instance is now the leader!"
            : "This instance is no longer the leader.");
    }
}
