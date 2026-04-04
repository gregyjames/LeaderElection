using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeaderElection.DistributedCache;

public sealed record ServiceBuilder(
    string? ServiceKey,
    OptionsBuilder<DistributedCacheSettings> OptionsBuilder
);

public static class DistributedCacheServiceBuilderExtensions
{
    /// <inheritdoc cref="AddDistributedCacheLeaderElectionInternal"/>
    /// <param name="configureOptions">Action to configure the DistributedCacheSettings</param>
    public static IServiceCollection AddDistributedCacheLeaderElection(
        this IServiceCollection services,
        Action<DistributedCacheSettings> configureOptions
    ) =>
        AddDistributedCacheLeaderElectionInternal(
            services,
            builder => builder.WithSettings(configureOptions)
        );

    /// <inheritdoc cref="AddDistributedCacheLeaderElectionInternal"/>
    /// <param name="options">DistributedCacheSettings instance to use for configuration</param>
    public static IServiceCollection AddDistributedCacheLeaderElection(
        this IServiceCollection services,
        DistributedCacheSettings options
    ) =>
        AddDistributedCacheLeaderElectionInternal(
            services,
            builder => builder.WithSettings(opts => DistributedCacheSettings.Copy(options, opts))
        );

    /// <inheritdoc cref="AddDistributedCacheLeaderElectionInternal"/>
    public static IServiceCollection AddDistributedCacheLeaderElection(
        this IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    ) => AddDistributedCacheLeaderElectionInternal(services, builder, serviceKey);

    /// <summary>
    /// Adds DistributedCache leader election services to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="builder">Action to configure the service builder</param>
    /// <param name="serviceKey">Optional service key for keyed services</param>
    /// <returns>The service collection for chaining</returns>
    private static IServiceCollection AddDistributedCacheLeaderElectionInternal(
        this IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(builder);

        var configBuilder = services.AddOptionsWithValidateOnStart<
            DistributedCacheSettings,
            DistributedCacheSettingsValidator
        >(serviceKey);

        services.AddKeyedSingleton<ILeaderElection>(
            serviceKey,
            static (sp, key) =>
            {
                // get settings
                var settings = sp.GetRequiredService<IOptionsMonitor<DistributedCacheSettings>>()
                    .Get(key as string);

                // Note: We must resolve the cache here. If we don't do it now
                // and the factory resolves it from DI, then the cache (or its dependencies)
                // may be disposed before the LeaderElection resulting in a crash.
                var cache =
                    (
                        settings.CacheFactory
                        ?? throw new InvalidOperationException(
                            "CacheFactory must be specified in settings."
                        )
                    ).Invoke(settings)
                    ?? throw new InvalidOperationException("CacheFactory returned null.");

                // create instance
                return ActivatorUtilities.CreateInstance<DistributedCacheLeaderElection>(
                    sp,
                    settings,
                    cache
                );
            }
        );

        builder.Invoke(new ServiceBuilder(serviceKey, configBuilder));

        // Ensure CacheFactory is set after all configurations
        configBuilder.PostConfigure<IServiceProvider>(
            (opts, sp) => opts.CacheFactory ??= _ => GetRegisteredCache(serviceKey, sp)
        );

        return services;
    }

    /// <summary>
    /// Specifies the instance ID to use for the Leader Election.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="instanceId"></param>
    /// <returns></returns>
    public static ServiceBuilder WithInstanceId(this ServiceBuilder builder, string instanceId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(instanceId);
        return builder.WithSettings(options => options.InstanceId = instanceId);
    }

    /// <summary>
    /// Configures the leader election settings using a configuration section from the
    /// application's configuration.
    /// </summary>
    public static ServiceBuilder WithConfiguration(
        this ServiceBuilder builder,
        string configurationSectionName
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configurationSectionName);
        builder.OptionsBuilder.Configure<IConfiguration>(
            (opts, config) => config.GetSection(configurationSectionName).Bind(opts)
        );
        return builder;
    }

    /// <summary>
    /// Configures the leader election settings using a configuration section from the
    /// application's configuration.
    /// </summary>
    public static ServiceBuilder WithConfiguration(
        this ServiceBuilder builder,
        IConfiguration configurationSection
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configurationSection);
        builder.OptionsBuilder.Configure(configurationSection.Bind);
        return builder;
    }

    /// <summary>
    /// Configures the leader election settings using a configuration action.
    /// </summary>
    public static ServiceBuilder WithSettings(
        this ServiceBuilder builder,
        Action<DistributedCacheSettings> configureOptions
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        builder.OptionsBuilder.Configure(configureOptions);
        return builder;
    }

    /// <summary>
    /// Configures the leader election to use a registered IDistributedCache instance
    /// from the service provider.
    /// </summary>
    public static ServiceBuilder WithRegisteredCache(this ServiceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithSettings(
            (opts, sp, key) => opts.CacheFactory = _ => GetRegisteredCache(key, sp)
        );
    }

    /// <summary>
    /// Configures the leader election to use the specified IDistributedCache instance.
    /// </summary>
    public static ServiceBuilder WithCache(this ServiceBuilder builder, IDistributedCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return builder.WithSettings(opts => opts.CacheFactory = _ => cache);
    }

    /// <summary>
    /// Configures the leader election settings using a configuration action which receives
    /// the service provider and the optional service key.
    /// </summary>
    /// <remarks>
    /// This is the most flexible configuration method, allowing for complex scenarios such as:
    /// - Resolving different cache instances based on the service key
    /// - Accessing other services from the service provider to configure the settings
    /// - Dynamically determining configuration values at runtime
    /// </remarks>
    public static ServiceBuilder WithSettings(
        this ServiceBuilder builder,
        Action<DistributedCacheSettings, IServiceProvider, string?> configAction
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configAction);
        builder.OptionsBuilder.Configure<IServiceProvider>(
            (opts, sp) => configAction(opts, sp, builder.ServiceKey)
        );
        return builder;
    }

    private static IDistributedCache GetRegisteredCache(object? serviceKey, IServiceProvider sp) =>
        sp.GetKeyedService<IDistributedCache>(serviceKey)
        ?? sp.GetRequiredService<IDistributedCache>();
}
