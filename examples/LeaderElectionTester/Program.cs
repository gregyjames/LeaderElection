using LeaderElection.BlobStorage;
using LeaderElection.DistributedCache;
using LeaderElection.Redis;
using LeaderElectionTester;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(configureLogger => configureLogger.WriteTo.Console());

// Get settings from configuration
var settings = new Settings();
builder.Configuration.Bind(settings);

// Register Redis connection multiplexer as singleton
var lazyConnectionMultiplexer = new Lazy<IConnectionMultiplexer>(() => 
    ConnectionMultiplexer.Connect($"{settings.Redis.Host}:{settings.Redis.Port}")
);
builder.Services.AddSingleton(_ => lazyConnectionMultiplexer.Value);

////////////////////////////////////////////////////
// Configure RedisLeaderElection
////////////////////////////////////////////////////
builder.Services.AddRedisLeaderElection(options =>
{
    options.LockKey = "leader_election_tester";
    options.LeaseDuration = TimeSpan.FromSeconds(30);
    options.RenewInterval = TimeSpan.FromSeconds(10);
    options.RetryInterval = TimeSpan.FromSeconds(5);
    options.MaxRetryAttempts = 3;
    options.EnableGracefulShutdown = true;
});

////////////////////////////////////////////////////
// Configure DistributedCacheLeaderElection
////////////////////////////////////////////////////
// builder.Services.AddDistributedCacheLeaderElection(options =>
// {
//     options.InstanceId = $"{AppDomain.CurrentDomain.FriendlyName}-{Guid.NewGuid()}";
//     options.RenewInterval = TimeSpan.FromSeconds(10);
//     options.MaxRetryAttempts = 3;
//     options.EnableGracefulShutdown = true;
// })
// .AddStackExchangeRedisCache(options =>
// {
//     options.ConnectionMultiplexerFactory = () => Task.FromResult(lazyConnectionMultiplexer.Value);
// });

////////////////////////////////////////////////////
// Configure BlobStorageLeaderElection
////////////////////////////////////////////////////
// builder.Services.AddBlobStorageLeaderElection(options =>
// {
//     // blob test using azurite
//     options.ConnectionString =
//         "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";
//     options.BlobName = "leader_election_tester";
//     options.LeaseDuration = TimeSpan.FromSeconds(30);
//     options.RenewInterval = TimeSpan.FromSeconds(10);
//     options.RetryInterval = TimeSpan.FromSeconds(5);
//     options.MaxRetryAttempts = 3;
//     options.EnableGracefulShutdown = true;
// });

////////////////////////////////////////////////////
// Configure S3LeaderElection
////////////////////////////////////////////////////
// builder.Services.AddSingleton<IMinioClient>(sp => 
// {
//    return new MinioClient()
//        .WithEndpoint("localhost:9000")
//        .WithCredentials("accessKey", "secretKey")
//        .Build();
// });
// builder.Services.AddS3LeaderElection(options =>
// {
//     options.BucketName = "my-app-locks";
//     options.ObjectKey = "leader-lock.json";
//     options.LeaseDuration = TimeSpan.FromSeconds(30);
//     options.RenewInterval = TimeSpan.FromSeconds(10);
// });

builder.Services.AddHostedService<Service>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
