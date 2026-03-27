using Microsoft.Extensions.DependencyInjection;

namespace LeaderElection.FusionCache;

public static class FusionCacheServiceBuilderExtensions
{
    public static IServiceCollection AddFusionCacheLeaderElection(
        this IServiceCollection services,
        Action<FusionCacheSettings> configureOptions)
    {
        services.AddOptionsWithValidateOnStart<FusionCacheSettings, FusionCacheSettingsValidator>()
            .Configure(configureOptions);

        services.AddSingleton<ILeaderElection, FusionCacheLeaderElection>();
        return services;
    }

    public static IServiceCollection AddFusionCacheLeaderElection(
        this IServiceCollection services,
        FusionCacheSettings options)
    {
        services.AddFusionCacheLeaderElection(opt =>
        {
            opt.InstanceId = options.InstanceId;
            opt.LockExpiry = options.LockExpiry;
            opt.LockKey = options.LockKey;
            opt.EnableGracefulShutdown = options.EnableGracefulShutdown;
            opt.RenewInterval = options.RenewInterval;
            opt.RetryInterval = options.RetryInterval;
            opt.MaxRetryAttempts = options.MaxRetryAttempts;
        });

        return services;
    }
}


