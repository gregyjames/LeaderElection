using System.Data.Common;
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

        services.AddTransient<DataSourceCreator>();

        services.AddKeyedSingleton<PostgresLeaderElection>(
            serviceKey,
            static (sp, key) =>
            {
                // get settings
                var settings = sp.GetRequiredService<IOptionsMonitor<PostgresSettings>>()
                    .Get(key as string);

                // Note: We must resolve the DataSource here. If we don't do it now
                // and the factory resolves it from DI later, then the DataSource
                // may be disposed before the LeaderElection resulting in a crash.
                var datasource =
                    (
                        settings.DataSourceFactory
                        ?? throw new InvalidOperationException(
                            "DataSourceFactory must be specified in settings."
                        )
                    ).Invoke(settings)
                    ?? throw new InvalidOperationException("DataSourceFactory returned null.");

                // create instance
                return ActivatorUtilities.CreateInstance<PostgresLeaderElection>(
                    sp,
                    settings,
                    datasource
                );
            }
        );

        services.AddKeyedSingleton<ILeaderElection>(
            serviceKey,
            static (sp, key) => sp.GetRequiredKeyedService<PostgresLeaderElection>(key)
        );

        builder.Invoke(new ServiceBuilder(serviceKey, configBuilder));

        // resolve the datasource from DI if a DataSourceFactory or ConnectionString is not set
        configBuilder.PostConfigure<IServiceProvider>(
            (opts, sp) =>
            {
                opts.DataSourceFactory ??= settings =>
                {
                    // If a ConnectionString is provided, use a DataSourceCreator else use the registered DataSource
                    return !string.IsNullOrEmpty(settings.ConnectionString)
                        ? sp.GetRequiredService<DataSourceCreator>().CreateDataSource(settings)
                        : GetRegisteredDataSource(sp, serviceKey);
                };
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
    /// <param name="builder">The service builder.</param>
    /// <param name="instanceId">The instance ID to use.</param>
    /// <returns>The service builder.</returns>
    public static ServiceBuilder WithInstanceId(this ServiceBuilder builder, string instanceId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(instanceId);
        return builder.WithSettings(options => options.InstanceId = instanceId);
    }

    /// <summary>
    /// Configures the leader election to use the <see cref="NpgsqlDataSource"/> registered
    /// in the DI container.
    /// </summary>
    /// <param name="builder">The service builder.</param>
    /// <returns>The service builder.</returns>
    public static ServiceBuilder WithRegisteredDataSource(this ServiceBuilder builder)
    {
        return builder.WithSettings(
            (opts, sp, key) => opts.DataSourceFactory = _ => GetRegisteredDataSource(sp, key)
        );
    }

    /// <summary>
    /// Configures the leader election to use the <see cref="NpgsqlDataSource"/> registered
    /// in the DI container with the specified service key.
    /// </summary>
    /// <param name="builder">The service builder.</param>
    /// <param name="datasourceServiceKey">The service key of the specific <see cref="NpgsqlDataSource"/>
    /// instance to resolve. Use <c>null</c> to resolve the default instance.</param>
    /// <returns>The service builder.</returns>
    public static ServiceBuilder WithRegisteredDataSource(
        this ServiceBuilder builder,
        object? datasourceServiceKey
    )
    {
        return builder.WithSettings(
            (opts, sp, key) =>
                opts.DataSourceFactory = _ =>
                    sp.GetRequiredKeyedService<NpgsqlDataSource>(datasourceServiceKey)
        );
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

    private static NpgsqlDataSource GetRegisteredDataSource(
        IServiceProvider sp,
        object? serviceKey = null
    ) =>
        (serviceKey != null ? sp.GetKeyedService<NpgsqlDataSource>(serviceKey) : null)
        ?? sp.GetRequiredService<NpgsqlDataSource>();

    // A private class used to create and dispose a DataSource when the
    // connection string settings are used. This class is registered with a transient
    // lifetime, so a new instance will be created each time it's needed, and it will
    // be disposed after use. In our use case (creating a singleton LeaderElection),
    // the DataSourceCreator's lifetime will be extended to match the LeaderElection.
    private class DataSourceCreator : IAsyncDisposable
    {
        private NpgsqlDataSource? _dataSource;

        public NpgsqlDataSource CreateDataSource(PostgresSettings settings)
        {
            ArgumentNullException.ThrowIfNullOrEmpty(settings.ConnectionString);
            var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);

            // Let's configure the DataSource with some sane defaults for LeaderElection
            // use cases (e.g. advisory locks). The user can always override these settings
            // by including them in the connection string or by providing their own
            // DataSourceFactory.
            // NpgsqlConnectionStringBuilder pre-populates some keys making it impossible
            // to check if they were set by the user or not, so use DbConnectionStringBuilder
            // to check for the presence of keys.
            var rawCheck = new DbConnectionStringBuilder
            {
                ConnectionString = settings.ConnectionString,
            };

            // In multi-host configurations, Npgsql will automatically route to a backup if the primary
            // is unavailable, resulting in unpredictable/orphaned locks. To avoid this, always specify
            // a TargetSessionAttributes value...
            if (
                builder.ConnectionStringBuilder.Host?.Contains(
                    ',',
                    StringComparison.OrdinalIgnoreCase
                ) == true
            )
            {
                // Connect to the primary (read-write) server (advisory locks do not replicate)
                // "primary" also works, although the primary is not always writable, so
                // "read-write" is safer when the connection might be used for more than locking.
                if (
                    !rawCheck.ContainsKey("TargetSessionAttributes")
                    && !rawCheck.ContainsKey("Target Session Attributes")
                    //spell:ignore PGTARGETSESSIONATTRS
                    && Environment.GetEnvironmentVariable("PGTARGETSESSIONATTRS") == null
                )
                {
                    builder.ConnectionStringBuilder.TargetSessionAttributes = "read-write";
                }
            }

            // Use short timeouts to avoid long waits if the database becomes unresponsive.
            if (!rawCheck.ContainsKey("Timeout"))
                builder.ConnectionStringBuilder.Timeout = 5; // Connection timeout
            if (!rawCheck.ContainsKey("CommandTimeout") && !rawCheck.ContainsKey("Command Timeout"))
                builder.ConnectionStringBuilder.CommandTimeout = 3;

            _dataSource = builder.Build();

            // Update settings with the final connection string (including any defaults we applied)
            settings.ConnectionString = _dataSource.ConnectionString;

            return _dataSource;
        }

        public ValueTask DisposeAsync() => _dataSource?.DisposeAsync() ?? new();
    }
}
