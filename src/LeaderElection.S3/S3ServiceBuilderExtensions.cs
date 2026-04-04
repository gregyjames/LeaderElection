using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minio;

namespace LeaderElection.S3;

public sealed record ServiceBuilder(
    IServiceCollection Services,
    string? ServiceKey,
    OptionsBuilder<S3Settings> OptionsBuilder
);

public static class S3ServiceBuilderExtensions
{
    /// <inheritdoc cref="AddS3LeaderElectionInternal"/>
    /// <exception cref="ArgumentNullException">Thrown if the configureOptions parameter is null.</exception>
    public static IServiceCollection AddS3LeaderElection(
        this IServiceCollection services,
        Action<S3Settings> configureOptions
    ) => AddS3LeaderElection(services, builder => builder.WithSettings(configureOptions));

    /// <inheritdoc cref="AddS3LeaderElectionInternal"/>
    /// <param name="options">The S3Settings used by the Leader Election.</param>
    /// <exception cref="ArgumentNullException">Thrown if the options parameter is null.</exception>
    public static IServiceCollection AddS3LeaderElection(
        this IServiceCollection services,
        S3Settings options
    ) => AddS3LeaderElection(services, builder => builder.WithSettings(options));

    /// <inheritdoc cref="AddS3LeaderElectionInternal"/>
    /// <remarks>
    /// This overload allows for named registrations of the leader election services
    /// and settings, enabling multiple leader election registrations in the same
    /// application.
    /// </remarks>
    public static IServiceCollection AddS3LeaderElection(
        this IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    ) => AddS3LeaderElectionInternal(services, builder, serviceKey);

    /// <summary>
    /// Adds S3-based leader election services to the specified specified
    /// <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="builder">An action used to configure the settings.</param>
    /// <param name="serviceKey">An optional key to register the services and settings
    /// with. This allows for multiple leader election registrations in the same
    /// application. If not provided, the services and settings will be registered
    /// without a key.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddS3LeaderElectionInternal(
        IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(builder);

        // Register options and leader election services with the specified service key
        var optionsBuilder = services.AddOptionsWithValidateOnStart<
            S3Settings,
            S3SettingsValidator
        >(serviceKey);

        services.AddKeyedSingleton(
            serviceKey,
            static (sp, key) =>
            {
                // get settings
                var settings = sp.GetRequiredService<IOptionsSnapshot<S3Settings>>()
                    .Get(key as string);

                // Note: We must resolve the Minio client here. If we don't do it now
                // and the factory resolves it from DI, then the Minio client (or its dependencies)
                // may be disposed before the LeaderElection resulting in a crash.
                var minioClient =
                    (
                        settings.MinioClientFactory
                        ?? throw new InvalidOperationException(
                            "MinioClientFactory must be specified in settings."
                        )
                    ).Invoke(settings)
                    ?? throw new InvalidOperationException("MinioClientFactory returned null.");

                return ActivatorUtilities.CreateInstance<S3LeaderElection>(
                    sp,
                    settings,
                    minioClient
                );
            }
        );

        services.AddKeyedSingleton<ILeaderElection>(
            serviceKey,
            static (sp, key) => sp.GetRequiredKeyedService<S3LeaderElection>(key)
        );

        // Invoke builder action to allow user to specify a minio client
        // factory and settings
        builder.Invoke(new ServiceBuilder(services, serviceKey, optionsBuilder));

        // Ensure the MinioClientFactory is set. If not, configure
        // it using DI, first trying to resolve a keyed instance.
        optionsBuilder.PostConfigure<IServiceProvider>(
            (opts, sp) => opts.MinioClientFactory ??= _ => GetRegisteredMinioClient(sp, serviceKey)
        );

        return services;
    }

    //
    // Extension methods for configuring the S3Settings.
    //

    /// <summary>
    /// Specifies a Configuration section to bind to the default
    /// <see cref="S3Settings"/> used by the Leader Election.
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
    /// <see cref="S3Settings"/> used by the Leader Election.
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
    /// Specifies the default <see cref="S3Settings"/> used by
    /// the Leader Election.
    /// </summary>
    public static ServiceBuilder WithSettings(this ServiceBuilder builder, S3Settings settings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);
        builder.OptionsBuilder.Configure(dst => S3Settings.Copy(settings, dst));
        return builder;
    }

    /// <summary>
    /// Specifies an action to configure the <see cref="S3Settings"/>
    /// used by the Leader Election.
    /// </summary>
    public static ServiceBuilder WithSettings(
        this ServiceBuilder builder,
        Action<S3Settings> configureOptions
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        builder.OptionsBuilder.Configure(configureOptions);
        return builder;
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
        Action<S3Settings, IServiceProvider, string?> configAction
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
    /// Specifies a specific IMinioClient to use with the Leader Election.
    /// </summary>
    public static ServiceBuilder WithMinioClient(
        this ServiceBuilder builder,
        IMinioClient minioClient
    )
    {
        ArgumentNullException.ThrowIfNull(minioClient);
        return builder.WithMinioClientFactory(_ => minioClient);
    }

    /// <summary>
    /// Specifies a IMinioClient factory function used by the Leader Election.
    /// </summary>
    public static ServiceBuilder WithMinioClientFactory(
        this ServiceBuilder builder,
        Func<S3Settings, IMinioClient> minioClientFactory
    )
    {
        ArgumentNullException.ThrowIfNull(minioClientFactory);
        return builder.WithSettings(options => options.MinioClientFactory = minioClientFactory);
    }

    /// <summary>
    /// Specifies that the Leader Election should attempt to resolve an IMinioClient
    /// from the DI container, first trying to resolve a keyed instance with the same
    /// service key as the leader election registration, then falling back to an unkeyed
    /// IMinioClient. This is the default behavior when no MinioClientFactory is configured.
    /// </summary>
    public static ServiceBuilder WithRegisteredMinioClient(this ServiceBuilder builder)
    {
        return builder.WithSettings(
            static (opts, sp, serviceKey) =>
                opts.MinioClientFactory = _ => GetRegisteredMinioClient(sp, serviceKey)
        );
    }

    private static IMinioClient GetRegisteredMinioClient(IServiceProvider sp, string? serviceKey)
    {
        return sp.GetKeyedService<IMinioClient>(serviceKey)
            ?? sp.GetRequiredService<IMinioClient>();
    }
}
