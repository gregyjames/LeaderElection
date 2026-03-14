using Microsoft.Extensions.DependencyInjection;

namespace LeaderElection.FusionCache;

public static class FusionCacheServiceBuilderExtensions
{
    public static IServiceCollection AddFusionCacheLeaderElection(
        this IServiceCollection services,
        Action<FusionCacheSettings>? configureOptions = null)
    {
        if (configureOptions != null)
            services.Configure(configureOptions);

        services.AddSingleton<ILeaderElection, FusionCacheLeaderElection>();
        return services;
    }
}


