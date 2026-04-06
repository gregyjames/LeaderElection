using Microsoft.Extensions.DependencyInjection;

namespace LeaderElection.Redis;

public static class RedisServiceBuilderExtensions
{
    public static IServiceCollection AddRedisLeaderElection(
        this IServiceCollection services,
        Action<RedisSettings> configureOptions
    )
    {
        services
            .AddOptionsWithValidateOnStart<RedisSettings, RedisSettingsValidator>()
            .Configure(configureOptions);

        services.AddSingleton<RedisLeaderElection>();
        services.AddSingleton<ILeaderElection>(sp => sp.GetRequiredService<RedisLeaderElection>());

        return services;
    }

    public static IServiceCollection AddRedisLeaderElection(
        this IServiceCollection services,
        RedisSettings options
    )
    {
        services.AddRedisLeaderElection(opt =>
        {
            opt.Host = options.Host;
            opt.Port = options.Port;
            opt.Password = options.Password;
            opt.Database = options.Database;
            opt.LockKey = options.LockKey;
            opt.InstanceId = options.InstanceId;
            opt.LockExpiry = options.LockExpiry;
            opt.RenewInterval = options.RenewInterval;
            opt.RetryInterval = options.RetryInterval;
            opt.MaxRetryAttempts = options.MaxRetryAttempts;
            opt.EnableGracefulShutdown = options.EnableGracefulShutdown;
        });

        return services;
    }
}
