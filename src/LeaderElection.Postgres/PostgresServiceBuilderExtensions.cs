using Microsoft.Extensions.DependencyInjection;

namespace LeaderElection.Postgres;

public static class PostgresServiceBuilderExtensions
{
    public static IServiceCollection AddPostgresLeaderElection(
        this IServiceCollection services, 
        Action<PostgresSettings> configureOptions)
    {
        services.Configure(configureOptions);
        services.AddSingleton<PostgresLeaderElection>();
        services.AddSingleton<ILeaderElection>(sp => sp.GetRequiredService<PostgresLeaderElection>());
        
        return services;
    }

    public static IServiceCollection AddPostgresLeaderElection(
        this IServiceCollection services,
        PostgresSettings options)
    {
        services.AddPostgresLeaderElection(opt =>
        {
            opt.InstanceId = options.InstanceId;
            opt.ConnectionString = options.ConnectionString;
            opt.LockId = options.LockId;
            opt.RetryInterval = options.RetryInterval;
            opt.EnableGracefulShutdown = options.EnableGracefulShutdown;
        });
        
        return services;
    }
}
