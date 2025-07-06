using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeaderElection.BlobStorage;

public static class BlobStorageServiceBuilderExtensions
{
    public static IServiceCollection AddBlobStorageLeaderElection(
        this IServiceCollection services, 
        Action<BlobStorageSettings> configureOptions)
    {
        services.Configure(configureOptions);
        
        services.AddSingleton<BlobServiceClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BlobStorageSettings>>().Value;
            return new BlobServiceClient(options.ConnectionString);
        });
        
        services.AddSingleton<BlobStorageLeaderElection>();
        services.AddSingleton<ILeaderElection>(sp => sp.GetRequiredService<BlobStorageLeaderElection>());

        return services;
    }

    public static IServiceCollection AddBlobStorageLeaderElection(
        this IServiceCollection services,
        BlobStorageSettings options)
    {
        services.Configure<BlobStorageSettings>(opt =>
        {
            opt.ConnectionString = options.ConnectionString;
            opt.ContainerName = options.ContainerName;
            opt.BlobName = options.BlobName;
            opt.InstanceId = options.InstanceId;
            opt.LeaseDuration = options.LeaseDuration;
            opt.RenewInterval = options.RenewInterval;
            opt.RetryInterval = options.RetryInterval;
            opt.MaxRetryAttempts = options.MaxRetryAttempts;
            opt.EnableGracefulShutdown = options.EnableGracefulShutdown;
            opt.CreateContainerIfNotExists = options.CreateContainerIfNotExists;
        });
        
        services.AddSingleton<BlobServiceClient>(new BlobServiceClient(options.ConnectionString));
        services.AddSingleton<BlobStorageLeaderElection>();
        services.AddSingleton<ILeaderElection>(sp => sp.GetRequiredService<BlobStorageLeaderElection>());

        
        return services;
    }
    
    public static IServiceCollection AddBlobStorageLeaderElection(
        this IServiceCollection services,
        string endpoint,
        BlobStorageSettings options)
    {
        services.Configure<BlobStorageSettings>(opt =>
        {
            opt.ConnectionString = options.ConnectionString;
            opt.ContainerName = options.ContainerName;
            opt.BlobName = options.BlobName;
            opt.InstanceId = options.InstanceId;
            opt.LeaseDuration = options.LeaseDuration;
            opt.RenewInterval = options.RenewInterval;
            opt.RetryInterval = options.RetryInterval;
            opt.MaxRetryAttempts = options.MaxRetryAttempts;
            opt.EnableGracefulShutdown = options.EnableGracefulShutdown;
            opt.CreateContainerIfNotExists = options.CreateContainerIfNotExists;
        });
        
        services.AddSingleton<BlobServiceClient>(new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential()));
        services.AddSingleton<BlobStorageLeaderElection>();
        services.AddSingleton<ILeaderElection>(sp => sp.GetRequiredService<BlobStorageLeaderElection>());
        
        return services;
    }
} 