using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace LeaderElection.Redis;

public sealed record ServiceBuilder(
    string? ServiceKey,
    OptionsBuilder<RedisSettings> OptionsBuilder
);

public static class RedisServiceBuilderExtensions
{
    /// <inheritdoc cref="AddRedisLeaderElectionInternal" />
    /// <remarks>
    /// This overload registers an unnamed ILeaderElection service which uses the
    /// default Redis connection multiplexer factory (uses the host/port setting
    /// if specified, otherwise it will use the registered IConnectionMultiplexer).
    /// </remarks>
    /// <param name="configureOptions">An action to configure the <see cref="RedisSettings"/>
    /// used by the Leader Election.</param>
    public static IServiceCollection AddRedisLeaderElection(
        this IServiceCollection services,
        Action<RedisSettings> configureOptions
    ) => services.AddRedisLeaderElection(builder => builder.WithSettings(configureOptions));

    /// <inheritdoc cref="AddRedisLeaderElectionInternal" />
    /// <remarks>
    /// This overload registers an unnamed ILeaderElection service which uses the
    /// default Redis connection multiplexer factory (uses the host/port setting
    /// if specified, otherwise it will use the registered IConnectionMultiplexer).
    /// </remarks>
    /// <param name="options">The <see cref="RedisSettings"/> used by the Leader Election.</param>
    public static IServiceCollection AddRedisLeaderElection(
        this IServiceCollection services,
        RedisSettings options
    ) => services.AddRedisLeaderElection(builder => builder.WithSettings(options));

    /// <inheritdoc cref="AddRedisLeaderElectionInternal" />
    public static IServiceCollection AddRedisLeaderElection(
        this IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    ) => AddRedisLeaderElectionInternal(services, builder, serviceKey);

    /// <summary>
    /// Adds Redis based leader election services to the specified
    /// <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="builder">An action used to configure the settings and Redis
    /// connection multiplexer factory.</param>
    /// <param name="serviceKey">An optional key to register the services and settings
    /// with. This allows for multiple leader election registrations in the same
    /// application. If not provided, the services and settings will be registered
    /// without a key.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddRedisLeaderElectionInternal(
        IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(builder);

        // Register options and leader election services with the specified service key
        // (or no key if serviceKey is null)
        var optionsBuilder = services.AddOptionsWithValidateOnStart<
            RedisSettings,
            RedisSettingsValidator
        >(serviceKey);

        // Register a factory for creating and disposing a Redis connection multiplexer
        // when the host/port settings are used. This is needed to ensure the connection
        // multiplexer is disposed.
        services.AddTransient<ConnectionMultiplexerCreator>();

        // Register the RedisLeaderElection as a keyed singleton. The factory
        // creates a new instance of the RedisLeaderElection for each unique
        // service key, using the corresponding settings and Redis connection multiplexer.
        // Note: AddKeyedSingleton(serviceKey: null) is the same as AddSingleton().
        services.AddKeyedSingleton(
            serviceKey,
            static (sp, key) =>
            {
                // get settings
                var settings = sp.GetRequiredService<IOptionsMonitor<RedisSettings>>()
                    .Get(key as string);

                // Note: We must resolve the connection multiplexer here. If we don't do it now
                // and the factory resolves it from DI, then the connection multiplexer will be
                // disposed before the LeaderElection resulting in a crash.
                var connectionMultiplexer =
                    (
                        settings.ConnectionMultiplexerFactory
                        ?? throw new InvalidOperationException(
                            "ConnectionMultiplexerFactory must be specified in settings."
                        )
                    ).Invoke(settings)
                    ?? throw new InvalidOperationException(
                        "ConnectionMultiplexerFactory returned null."
                    );

                // create a new instance...
                return ActivatorUtilities.CreateInstance<RedisLeaderElection>(
                    sp,
                    settings,
                    connectionMultiplexer
                );
            }
        );

        services.AddKeyedSingleton<ILeaderElection>(
            serviceKey,
            static (sp, key) => sp.GetRequiredKeyedService<RedisLeaderElection>(key)
        );

        // Invoke builder action to allow user to specify a connection multiplexer
        // factory and settings
        builder(new ServiceBuilder(serviceKey, optionsBuilder));

        // Ensure a default connection multiplexer factory is specified...
        optionsBuilder.PostConfigure<IServiceProvider>(
            (opts, sp) =>
            {
                opts.ConnectionMultiplexerFactory ??= settings =>
                    GetDefaultConnectionMultiplexer(settings, sp, serviceKey);
            }
        );

        return services;
    }

    //
    // Extension methods for specifying the RedisClient factory for the
    // RedisLeaderElection builder
    //

    /// <summary>
    /// Specifies a Configuration section to bind to the default
    /// <see cref="RedisSettings"/> used by the Leader Election.
    /// </summary>
    public static ServiceBuilder WithConfiguration(
        this ServiceBuilder builder,
        IConfiguration configureSection
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureSection);
        builder.OptionsBuilder.Configure(configureSection.Bind);
        return builder;
    }

    /// <summary>
    /// Specifies a Configuration section name to bind to the default
    /// <see cref="RedisSettings"/> used by the Leader Election.
    /// </summary>
    public static ServiceBuilder WithConfiguration(
        this ServiceBuilder builder,
        string configurationSectionName
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configurationSectionName);
        builder.OptionsBuilder.Configure<IConfiguration>(
            (opts, configuration) => configuration.GetSection(configurationSectionName).Bind(opts)
        );
        return builder;
    }

    /// <summary>
    /// Specifies an action to configure the <see cref="RedisSettings"/>
    /// used by the Leader Election.
    /// </summary>
    public static ServiceBuilder WithSettings(
        this ServiceBuilder builder,
        Action<RedisSettings> configureOptions
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        builder.OptionsBuilder.Configure(configureOptions);
        return builder;
    }

    /// <summary>
    /// Specifies the <see cref="RedisSettings"/> used by the Leader Election.
    /// </summary>
    public static ServiceBuilder WithSettings(this ServiceBuilder builder, RedisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return builder.WithSettings(opts => RedisSettings.Copy(settings, opts));
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
        Action<RedisSettings, IServiceProvider, string?> configAction
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configAction);
        builder.OptionsBuilder.Configure<IServiceProvider>(
            (opts, sp) => configAction(opts, sp, builder.ServiceKey)
        );
        return builder;
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
    /// Configures the leader election to use the connection multiplexer registered
    /// in the service provider.
    /// </summary>
    public static ServiceBuilder WithRegisteredCache(this ServiceBuilder builder) =>
        builder.WithSettings(
            (opts, sp, key) =>
                opts.ConnectionMultiplexerFactory = _ => GetRegisteredConnectionMultiplexer(sp, key)
        );

    /// <summary>
    /// Specifies the connection multiplexer factory to use with the Leader Election.
    /// </summary>
    /// <remarks>
    /// This caller is responsible for ensuring the connection multiplexer is properly
    /// disposed since it will be resolved from the factory and not tracked by the DI container.
    /// </remarks>
    public static ServiceBuilder WithConnectionMultiplexer(
        this ServiceBuilder builder,
        IConnectionMultiplexer connectionMultiplexer
    )
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        return builder.WithConnectionMultiplexerFactory((_, _) => connectionMultiplexer);
    }

    /// <summary>
    /// Specifies the connection multiplexer factory to use with the Leader Election.
    /// </summary>
    public static ServiceBuilder WithConnectionMultiplexerFactory(
        this ServiceBuilder builder,
        Func<IServiceProvider, RedisSettings, IConnectionMultiplexer> factoryFunc
    )
    {
        ArgumentNullException.ThrowIfNull(factoryFunc);
        return builder.WithSettings(
            (opts, sp, _) =>
                opts.ConnectionMultiplexerFactory = settings => factoryFunc(sp, settings)
        );
    }

    // A private class used to create and dispose a Redis connection multiplexer when the
    // host/port settings are used.
    private class ConnectionMultiplexerCreator : IDisposable
    {
        public ConnectionMultiplexer? _connectionMultiplexer;

        public ConnectionMultiplexer CreateMultiplexer(RedisSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (string.IsNullOrWhiteSpace(settings.Host))
            {
                throw new InvalidOperationException(
                    "Host must be specified in settings to use the default ConnectionMultiplexer factory."
                );
            }

            _connectionMultiplexer = ConnectionMultiplexer.Connect(
                new ConfigurationOptions
                {
                    EndPoints = { { settings.Host, settings.Port } },
                    Password = settings.Password,
                    DefaultDatabase = settings.Database,
                }
            );
            return _connectionMultiplexer;
        }

        public void Dispose() => _connectionMultiplexer?.Dispose();
    }

    private static IConnectionMultiplexer GetDefaultConnectionMultiplexer(
        RedisSettings settings,
        IServiceProvider sp,
        string? serviceKey
    )
    {
        // If host is specified, use the default factory which creates a new connection multiplexer
        if (!string.IsNullOrWhiteSpace(settings.Host))
        {
            return sp.GetRequiredService<ConnectionMultiplexerCreator>()
                .CreateMultiplexer(settings);
        }

        return GetRegisteredConnectionMultiplexer(sp, serviceKey);
    }

    private static IConnectionMultiplexer GetRegisteredConnectionMultiplexer(
        IServiceProvider sp,
        string? serviceKey
    ) =>
        sp.GetKeyedService<IConnectionMultiplexer>(serviceKey)
        ?? sp.GetRequiredService<IConnectionMultiplexer>();
}
