using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeaderElection.S3;

public static class S3ServiceBuilderExtensions
{
    public static IServiceCollection AddS3LeaderElection(
        this IServiceCollection services,
        Action<S3Settings> configureOptions)
    {
        services.AddOptionsWithValidateOnStart<S3Settings, S3SettingsValidator>()
            .Configure(configureOptions);

        services.AddSingleton<S3LeaderElection>();
        services.AddSingleton<ILeaderElection>(sp => sp.GetRequiredService<S3LeaderElection>());

        return services;
    }

    public static IServiceCollection AddS3LeaderElection(
        this IServiceCollection services,
        S3Settings options)
    {
        services.AddS3LeaderElection(opt =>
        {
            opt.InstanceId = options.InstanceId;
            opt.BucketName = options.BucketName;
            opt.LeaseDuration = options.LeaseDuration;
            opt.RetryInterval = options.RetryInterval;
            opt.RenewInterval = options.RenewInterval;
            opt.ObjectKey = options.ObjectKey;
            opt.EnableGracefulShutdown = options.EnableGracefulShutdown;
            opt.MaxRetryAttempts = options.MaxRetryAttempts;
        });

        return services;
    }
}