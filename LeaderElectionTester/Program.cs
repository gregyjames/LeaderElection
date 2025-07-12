using System.Reflection;
using LeaderElection.BlobStorage;
using LeaderElection.DistributedCache;
using LeaderElection.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using StackExchange.Redis;

namespace LeaderElectionTester;

class Program
{
    static void Main(string[] args)
    {
        Host.CreateDefaultBuilder(args).UseSerilog((context, configuration) =>
        {
            configuration.WriteTo.Console();
        }).ConfigureAppConfiguration(builder =>
        {
            builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        }).ConfigureServices((context, collection) =>
        {
            collection.Configure<Settings>(context.Configuration);
            collection.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<Settings>>().Value;
                return ConnectionMultiplexer.Connect(options.Redis.Host);
            });
            /*
            collection.AddRedisLeaderElection(settings =>
            {
                settings.LockKey = "leader_election_tester";
                settings.LockExpiry = TimeSpan.FromSeconds(30);
                settings.RenewInterval = TimeSpan.FromSeconds(10);
                settings.RetryInterval = TimeSpan.FromSeconds(5);
                settings.MaxRetryAttempts = 3;
                settings.EnableGracefulShutdown = true;
            });
            collection.AddBlobStorageLeaderElection(settings =>
            {
                // blob test using azurite
                settings.ConnectionString =
                    "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";
                settings.BlobName = "leader_election_tester";
                settings.LeaseDuration = TimeSpan.FromSeconds(30);
                settings.RenewInterval = TimeSpan.FromSeconds(10);
                settings.RetryInterval = TimeSpan.FromSeconds(5);
                settings.MaxRetryAttempts = 3;
                settings.EnableGracefulShutdown = true;
            });
            */
            collection.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = "192.168.0.117:6379";
            });
            collection.AddDistributedCacheLeaderElection(cfg =>
            {
                cfg.InstanceId = $"{AppDomain.CurrentDomain.FriendlyName}-{Guid.NewGuid().ToString()}";
                cfg.RenewInterval = TimeSpan.FromSeconds(10);
                cfg.MaxRetryAttempts = 3;
                cfg.EnableGracefulShutdown = true;
            });
            collection.AddHostedService<Service>();
        }).Build().Run();
    }
}