using LeaderElection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LeaderElection.Redis;

namespace LeaderElectionTester;

public class Service: BackgroundService
{
    private readonly ILogger<Service> _logger;
    private readonly ILeaderElection _election;
    private readonly Settings _options;

    public Service(ILogger<Service> logger, IOptions<Settings> options, ILeaderElection election)
    {
        _logger = logger;
        _election = election;
        _options = options.Value;
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
            await _election.StartAsync(stoppingToken);
            
            // Keep the service running and periodically check leadership status
            while (!stoppingToken.IsCancellationRequested)
            {
                await _election.RunTaskIfLeaderAsync(() =>
                {
                    _logger.LogInformation("Executing leader task at {Time}", DateTime.UtcNow);
                }, stoppingToken);
                
                _logger.LogInformation("Doing something else...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally
        {
            // Unsubscribe from events
            _election.LeadershipChanged -= OnLeadershipChanged;
            _election.ErrorOccurred -= OnErrorOccurred;
            
            // Stop the leader election
            await _election.StopAsync(stoppingToken);
        }
    }
    
    private void OnLeadershipChanged(object? sender, bool isLeader)
    {
        if (isLeader)
        {
            _logger.LogInformation("This instance is now the leader!");
        }
        else
        {
            _logger.LogInformation("This instance is no longer the leader.");
        }
    }
    
    private void OnErrorOccurred(object? sender, Exception exception)
    {
        _logger.LogError(exception, "Error occurred in leader election");
    }
}