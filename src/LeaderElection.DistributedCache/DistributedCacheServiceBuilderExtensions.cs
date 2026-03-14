using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeaderElection.DistributedCache;

public static class DistributedCacheServiceBuilderExtensions
{
    /// <summary>
    /// Adds DistributedCache leader election services to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Action to configure the options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDistributedCacheLeaderElection(
        this IServiceCollection services,
        Action<DistributedCacheSettings>? configureOptions = null)
    {
        if (configureOptions != null)
            services.Configure(configureOptions);
        
        services.AddSingleton<ILeaderElection, DistributedCacheLeaderElection>();
        return services;
    }
} 