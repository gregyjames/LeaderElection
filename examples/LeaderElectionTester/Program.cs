using LeaderElection.BlobStorage;
using LeaderElection.DistributedCache;
using LeaderElection.FusionCache;
using LeaderElection.Postgres;
using LeaderElection.Redis;
using LeaderElection.S3;
using LeaderElectionTester;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Minio;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

var builder = Host.CreateApplicationBuilder(args);

var leaderElectionType = (
    builder.Configuration["LeaderElectionType"] ?? "Redis"
).ToUpperInvariant() switch
{
    "REDIS" => "Redis",
    "DISTRIBUTEDCACHE" or "DC" => "DistributedCache",
    "FUSIONCACHE" or "FC" => "FusionCache",
    "BLOBSTORAGE" or "BLOB" => "BlobStorage",
    "S3" => "S3",
    "POSTGRES" => "Postgres",
#pragma warning disable CA2208
    _ => throw new ArgumentException(
        "Invalid LeaderElection type. Supported values are: Redis, DistributedCache (dc), FusionCache (fc), BlobStorage (blob), S3, Postgres.",
        // ReSharper disable once NotResolvedInText
        "LeaderElectionType"
    ),
#pragma warning restore CA2208
};

// Get a unique ID for each running instance (e.g. in different terminals or machines).
var instanceId =
    builder.Configuration["InstanceId"]
    ?? $"{AppDomain.CurrentDomain.FriendlyName}:{Environment.MachineName}:{Environment.ProcessId}";

var redisConfiguration = builder.Configuration.GetConnectionString("redis") ?? "localhost:6379";

if (leaderElectionType is "Redis")
{
    builder.Services.AddRedisServices(redisConfiguration);
    builder.Services.AddRedisLeaderElection(options =>
    {
        options.LockKey = "leader_election_tester_redis";
        options.InstanceId = instanceId;
    });
}
else if (leaderElectionType is "DistributedCache")
{
    builder.Services.AddRedisServices(redisConfiguration);
    builder.Services.AddDistributedCache();
    builder.Services.AddDistributedCacheLeaderElection(options =>
    {
        options.LockKey = "leader_election_tester_dc";
        options.InstanceId = instanceId;
    });
}
else if (leaderElectionType is "FusionCache")
{
    builder.Services.AddRedisServices(redisConfiguration);
    builder.Services.AddDistributedCache();
    builder
        .Services.AddFusionCache()
        .WithRegisteredDistributedCache()
        .WithSystemTextJsonSerializer();
    builder.Services.AddFusionCacheLeaderElection(options =>
    {
        options.LockKey = "leader_election_tester_fc";
        options.InstanceId = instanceId;
    });
}
else if (leaderElectionType is "BlobStorage")
{
    builder.Services.AddAzureClients(configure =>
    {
        // blob test using Azurite or Storage Emulator
        configure.AddBlobServiceClient(
            builder.Configuration["BLOBS_CONNECTIONSTRING"] ?? "UseDevelopmentStorage=true;"
        );
    });
    builder.Services.AddBlobStorageLeaderElection(options =>
    {
        options.ContainerName = "leader-election";
        options.BlobName = "leader_election_tester";
        options.InstanceId = instanceId;
    });
}
else if (leaderElectionType is "S3")
{
    var minioUri = new Uri(builder.Configuration["Minio:Endpoint"] ?? "http://localhost:9000");
    var minioEndpoint = $"{minioUri.Host}:{minioUri.Port}";
    var minioAccessKey = builder.Configuration["Minio:AccessKey"] ?? "accessKey";
    var minioSecretKey = builder.Configuration["Minio:SecretKey"] ?? "secretKey";
    var minioBucketName = builder.Configuration["Minio:BucketName"] ?? "my-app-locks";

    builder.Services.AddMinio(client =>
        client
            .WithEndpoint(minioEndpoint)
            .WithCredentials(minioAccessKey, minioSecretKey)
            .WithSSL(false)
            .Build()
    );
    builder.Services.AddS3LeaderElection(options =>
    {
        options.BucketName = minioBucketName;
        options.ObjectKey = "leader-lock.json";
        options.InstanceId = instanceId;
    });
}
else if (leaderElectionType is "Postgres")
{
    builder.Services.AddNpgsqlDataSource(
        builder.Configuration.GetConnectionString("mydb")
            ?? "Host=localhost;Database=mydb;Username=myuser;Password=mypassword"
    );

    builder.Services.AddPostgresLeaderElection(options =>
    {
        options.LockId = 1;
        options.InstanceId = instanceId;
    });
}

builder.Services.AddHostedService<Service>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);

internal static class ProgramExtensions
{
    public static IServiceCollection AddRedisServices(
        this IServiceCollection services,
        string redisConfiguration
    )
    {
        return services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConfiguration)
        );
    }

    public static IServiceCollection AddDistributedCache(this IServiceCollection services)
    {
        services.AddStackExchangeRedisCache(_ => { });
        services
            .AddOptions<RedisCacheOptions>()
            .Configure<IServiceProvider>(
                (options, serviceProvider) =>
                    options.ConnectionMultiplexerFactory = () =>
                        Task.FromResult(
                            serviceProvider.GetRequiredService<IConnectionMultiplexer>()
                        )
            );
        return services;
    }
}
