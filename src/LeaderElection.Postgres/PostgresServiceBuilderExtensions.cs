using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LeaderElection.Postgres;

public sealed record ServiceBuilder(
    string? ServiceKey,
    OptionsBuilder<PostgresSettings> OptionsBuilder
);

public static class PostgresServiceBuilderExtensions
{
    /// <inheritdoc cref="AddPostgresLeaderElectionInternal"/>
    /// <param name="configureOptions">Action to configure the PostgresSettings</param>
    public static IServiceCollection AddPostgresLeaderElection(
        this IServiceCollection services,
        Action<PostgresSettings> configureOptions
    ) =>
        AddPostgresLeaderElectionInternal(
            services,
            builder => builder.WithSettings(configureOptions)
        );

    /// <inheritdoc cref="AddPostgresLeaderElectionInternal"/>
    /// <param name="options">PostgresSettings instance to use for configuration</param>
    public static IServiceCollection AddPostgresLeaderElection(
        this IServiceCollection services,
        PostgresSettings options
    ) => AddPostgresLeaderElectionInternal(services, builder => builder.WithSettings(options));

    /// <inheritdoc cref="AddPostgresLeaderElectionInternal"/>
    public static IServiceCollection AddPostgresLeaderElection(
        this IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    ) => AddPostgresLeaderElectionInternal(services, builder, serviceKey);

    /// <summary>
    /// Adds Postgres leader election services to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="builder">Action to configure the service builder</param>
    /// <param name="serviceKey">Optional service key for keyed services</param>
    /// <returns>The service collection for chaining</returns>
    private static IServiceCollection AddPostgresLeaderElectionInternal(
        this IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(builder);

        var configBuilder = services.AddOptionsWithValidateOnStart<
            PostgresSettings,
            PostgresSettingsValidator
        >(serviceKey);

        services.AddKeyedSingleton<PostgresLeaderElection>(
            serviceKey,
            static (sp, key) =>
            {
                // get options
                var options = sp.GetRequiredService<IOptionsMonitor<PostgresSettings>>()
                    .Get(key as string);

                // create instance
                return ActivatorUtilities.CreateInstance<PostgresLeaderElection>(sp, options);
            }
        );

        services.AddKeyedSingleton<ILeaderElection>(
            serviceKey,
            static (sp, key) => sp.GetRequiredKeyedService<PostgresLeaderElection>(key)
        );

        builder.Invoke(new ServiceBuilder(serviceKey, configBuilder));

        // resolve the connection from DI if a ConnectionFactory or ConnectionString is not set
        configBuilder.PostConfigure<IServiceProvider>(
            (opts, sp) =>
            {
                if (opts.ConnectionFactory is null && string.IsNullOrEmpty(opts.ConnectionString))
                {
                    opts.ConnectionFactory = _ => GetRegisteredConnection(sp, serviceKey);
                }
            }
        );

        return services;
    }

    /// <summary>
    /// Configures the leader election settings using the specified configuration section.
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
    /// Configures the leader election settings using the specified configuration section.
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
    /// Specifies the <see cref="PostgresSettings"/> used by the Leader Election.
    /// </summary>
    public static ServiceBuilder WithSettings(
        this ServiceBuilder builder,
        PostgresSettings settings
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        return builder.WithSettings(opts => PostgresSettings.Copy(settings, opts));
    }

    /// <summary>
    /// Configures the leader election settings using the specified action.
    /// </summary>
    public static ServiceBuilder WithSettings(
        this ServiceBuilder builder,
        Action<PostgresSettings> configureOptions
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        builder.OptionsBuilder.Configure(configureOptions);
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
    /// Configures the leader election to use the NpgsqlConnection registered
    /// in the DI container.
    /// </summary>
    public static ServiceBuilder WithRegisteredConnection(this ServiceBuilder builder)
    {
        return builder.WithSettings(
            (opts, sp, key) => opts.ConnectionFactory = _ => GetRegisteredConnection(sp, key)
        );
    }

    /// <summary>
    /// Configures the leader election to use the specified connection factory.
    /// </summary>
    public static ServiceBuilder WithConnectionFactory(
        this ServiceBuilder builder,
        Func<PostgresSettings, NpgsqlConnection> connectionFactory
    )
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return builder.WithSettings(opts => opts.ConnectionFactory = connectionFactory);
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
        Action<PostgresSettings, IServiceProvider, string?> configAction
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configAction);
        builder.OptionsBuilder.Configure<IServiceProvider>(
            (opts, sp) => configAction(opts, sp, builder.ServiceKey)
        );
        return builder;
    }

    private static NpgsqlConnection GetRegisteredConnection(
        IServiceProvider sp,
        string? serviceKey = null
    )
    {
        return sp.GetKeyedService<NpgsqlConnection>(serviceKey)
            ?? sp.GetRequiredService<NpgsqlConnection>();
    }
}
