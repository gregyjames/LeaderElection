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

builder.Logging.AddSerilog();
builder.Services.AddSerilog(configureLogger => configureLogger.WriteTo.Console());

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
builder.Services.AddRedisLeaderElection(settings =>
{
    settings.LockKey = "leader_election_tester";
    settings.LockExpiry = TimeSpan.FromSeconds(30);
    settings.RenewInterval = TimeSpan.FromSeconds(10);
    settings.RetryInterval = TimeSpan.FromSeconds(5);
    settings.MaxRetryAttempts = 3;
    settings.EnableGracefulShutdown = true;
});

////////////////////////////////////////////////////
// Configure DistributedCacheLeaderElection
////////////////////////////////////////////////////
// builder.Services.AddDistributedCacheLeaderElection(cfg =>
// {
//     cfg.InstanceId = $"{AppDomain.CurrentDomain.FriendlyName}-{Guid.NewGuid()}";
//     cfg.RenewInterval = TimeSpan.FromSeconds(10);
//     cfg.MaxRetryAttempts = 3;
//     cfg.EnableGracefulShutdown = true;
// })
// .AddStackExchangeRedisCache(options =>
// {
//     options.ConnectionMultiplexerFactory = () => Task.FromResult(lazyConnectionMultiplexer.Value);
// });

////////////////////////////////////////////////////
// Configure BlobStorageLeaderElection
////////////////////////////////////////////////////
// builder.Services.AddBlobStorageLeaderElection(settings =>
// {
//     // blob test using azurite
//     settings.ConnectionString =
//         "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";
//     settings.BlobName = "leader_election_tester";
//     settings.LeaseDuration = TimeSpan.FromSeconds(30);
//     settings.RenewInterval = TimeSpan.FromSeconds(10);
//     settings.RetryInterval = TimeSpan.FromSeconds(5);
//     settings.MaxRetryAttempts = 3;
//     settings.EnableGracefulShutdown = true;
// });

builder.Services.AddHostedService<Service>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
