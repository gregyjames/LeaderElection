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
using Npgsql;
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
    builder.Services.AddRedisLeaderElection(builder =>
        builder
            .WithInstanceId(instanceId)
            .WithSettings(options =>
            {
                options.LockKey = "leader_election_tester_redis";
            })
    );
}
else if (leaderElectionType is "DistributedCache")
{
    builder.Services.AddRedisServices(redisConfiguration);
    builder.Services.AddDistributedCache();
    builder.Services.AddDistributedCacheLeaderElection(builder =>
        builder
            .WithInstanceId(instanceId)
            .WithSettings(options =>
            {
                options.LockKey = "leader_election_tester_dc";
            })
    );
}
else if (leaderElectionType is "FusionCache")
{
    builder.Services.AddRedisServices(redisConfiguration);
    builder.Services.AddDistributedCache();
    builder
        .Services.AddFusionCache()
        .WithRegisteredDistributedCache()
        .WithSystemTextJsonSerializer();
    builder.Services.AddFusionCacheLeaderElection(builder =>
        builder
            .WithInstanceId(instanceId)
            .WithSettings(options =>
            {
                options.LockKey = "leader_election_tester_fc";
            })
    );
}
else if (leaderElectionType is "BlobStorage")
{
    builder.Services.AddAzureClients(configure =>
    {
        // blob test using Azurite or Storage Emulator
        configure.AddBlobServiceClient(
            builder.Configuration.GetConnectionString("blobs") ?? "UseDevelopmentStorage=true;"
        );
    });
    builder.Services.AddBlobStorageLeaderElection(builder =>
        builder
            .WithInstanceId(instanceId)
            .WithSettings(options =>
            {
                options.ContainerName = "leader-election";
                options.BlobName = "leader_election_tester";
            })
    );
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

    builder.Services.AddS3LeaderElection(builder =>
        builder
            .WithInstanceId(instanceId)
            .WithSettings(options =>
            {
                options.BucketName = minioBucketName;
                options.ObjectKey = "leader-lock.json";
            })
    );
}
else if (leaderElectionType is "Postgres")
{
    var connectionString =
        builder.Configuration.GetConnectionString("mydb")
        ?? "Host=localhost;Database=mydb;Username=myuser;Password=mypassword;";

    // Register a NpgsqlDataSource specifically for Leader Election use...
    const string postgresLeaderElectionDataSource = "PostgresLeaderElectionDataSource";
    builder.Services.AddNpgsqlDataSource(
        // Use short CommandTimeout to avoid long waits if the database becomes unresponsive.
        connectionString + ";Timeout=5;CommandTimeout=3;",
        serviceKey: postgresLeaderElectionDataSource
    );

    builder.Services.AddPostgresLeaderElection(builder =>
        builder
            .WithRegisteredDataSource(postgresLeaderElectionDataSource)
            .WithInstanceId(instanceId)
            .WithSettings(options =>
            {
                options.LockId = 1;
                // Use a short RenewInterval to quickly detect and recover from failed leaders.
                // Note that the actual detection time will be at least the sum of the CommandTimeout
                // and RenewInterval, so keep CommandTimeout low as well.
                options.RenewInterval = TimeSpan.FromSeconds(5); // aggressive renew
            })
    );
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
