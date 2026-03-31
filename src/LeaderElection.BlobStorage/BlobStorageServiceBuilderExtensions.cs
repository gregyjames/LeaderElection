using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeaderElection.BlobStorage;

public sealed record ServiceBuilder(
    string? ServiceKey,
    OptionsBuilder<BlobStorageSettings> OptionsBuilder
);

public static class BlobStorageServiceBuilderExtensions
{
    /// <inheritdoc cref="AddBlobStorageLeaderElectionInternal" />
    /// <remarks>
    /// This overload registers an unnamed ILeaderElection service which uses the
    /// default BlobServiceClient factory (uses the ConnectionString setting
    /// if specified, otherwise it will use the registered BlobServiceClient).
    /// </remarks>
    /// <param name="configureOptions">An action to configure the
    /// <see cref="BlobStorageSettings"/> used by the Leader Election.</param>
    public static IServiceCollection AddBlobStorageLeaderElection(
        this IServiceCollection services,
        Action<BlobStorageSettings> configureOptions
    ) => services.AddBlobStorageLeaderElection(builder => builder.WithSettings(configureOptions));

    /// <inheritdoc cref="AddBlobStorageLeaderElectionInternal" />
    /// <remarks>
    /// This overload registers an unnamed ILeaderElection service which uses the
    /// default BlobServiceClient factory (uses the ConnectionString setting
    /// if specified, otherwise it will use the registered BlobServiceClient).
    /// </remarks>
    /// <param name="options">The <see cref="BlobStorageSettings"/> instance to use for
    /// configuration.</param>
    public static IServiceCollection AddBlobStorageLeaderElection(
        this IServiceCollection services,
        BlobStorageSettings options
    ) => services.AddBlobStorageLeaderElection(builder => builder.WithSettings(options));

    /// <inheritdoc cref="AddBlobStorageLeaderElectionInternal" />
    /// <remarks>
    /// This overload registers an unnamed ILeaderElection service which uses the
    /// default BlobServiceClient factory (uses the ConnectionString setting
    /// if specified, otherwise it will use the registered BlobServiceClient).
    /// </remarks>
    /// <param name="endpoint">The Blob Storage endpoint. <see cref="DefaultAzureCredential"/>
    /// will be used for authentication.</param>
    /// <param name="options">The <see cref="BlobStorageSettings"/> instance to use
    /// for configuration.</param>
    public static IServiceCollection AddBlobStorageLeaderElection(
        this IServiceCollection services,
        string endpoint,
        BlobStorageSettings options
    )
    {
        return services.AddBlobStorageLeaderElection(builder =>
        {
            builder.WithSettings(options);
            builder.WithBlobServiceClient(
                new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential())
            );
        });
    }

    /// <inheritdoc cref="AddBlobStorageLeaderElectionInternal" />
    public static IServiceCollection AddBlobStorageLeaderElection(
        this IServiceCollection services,
        Action<ServiceBuilder> builder,
        string? serviceKey = null
    ) => AddBlobStorageLeaderElectionInternal(services, builder, serviceKey);

    /// <summary>
    /// Adds Blob Storage based leader election services to the specified
    /// <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="builder">An action used to configure the settings and blob client factory.</param>
    /// <param name="serviceKey">An optional key to register the services and settings
    /// with. This allows for multiple leader election registrations in the same
    /// application. If not provided, the services and settings will be registered
    /// without a key.</param>
    /// <returns>The updated service collection.</returns>
    private static IServiceCollection AddBlobStorageLeaderElectionInternal(
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
            BlobStorageSettings,
            BlobStorageSettingsValidator
        >(serviceKey);

        // Register the BlobStorageLeaderElection as a keyed singleton. The factory
        // creates a new instance of the BlobStorageLeaderElection for each unique
        // service key, using the corresponding settings and blob client factory.
        // Note: AddKeyedSingleton(serviceKey: null) is the same as AddSingleton().
        services.AddKeyedSingleton(
            serviceKey,
            static (sp, key) =>
            {
                // get [keyed] settings
                var settings = sp.GetRequiredService<IOptionsSnapshot<BlobStorageSettings>>()
                    .Get(key as string);

                // create a new instance...
                return ActivatorUtilities.CreateInstance<BlobStorageLeaderElection>(sp, settings);
            }
        );

        services.AddKeyedSingleton<ILeaderElection>(
            serviceKey,
            static (sp, key) => sp.GetRequiredKeyedService<BlobStorageLeaderElection>(key)
        );

        // Invoke builder action to allow user to specify a blob client
        // factory and settings
        builder.Invoke(new ServiceBuilder(serviceKey, optionsBuilder));

        // Ensure a blob client factory is registered if no connection string is provided
        optionsBuilder.PostConfigure<IServiceProvider>(
            (opts, sp) =>
            {
                if (string.IsNullOrEmpty(opts.ConnectionString))
                {
                    opts.BlobClientFactory ??= (settings, ct) =>
                        CreateBlobClient(sp, serviceKey, settings, ct);
                }
            }
        );

        return services;
    }

    //
    // Extension methods for specifying the BlobClient factory for the
    // BlobStorageLeaderElection builder
    //

    /// <summary>
    /// Specifies a Configuration section to bind to the default
    /// <see cref="BlobStorageSettings"/> used by the Leader Election.
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
    /// <see cref="BlobStorageSettings"/> used by the Leader Election.
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
    /// Specifies the default <see cref="BlobStorageSettings"/> used by
    /// the Leader Election.
    /// </summary>
    public static ServiceBuilder WithSettings(
        this ServiceBuilder builder,
        BlobStorageSettings settings
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        return builder.WithSettings(dst => BlobStorageSettings.Copy(settings, dst));
    }

    /// <summary>
    /// Specifies an action to configure the <see cref="BlobStorageSettings"/>
    /// used by the Leader Election.
    /// </summary>
    public static ServiceBuilder WithSettings(
        this ServiceBuilder builder,
        Action<BlobStorageSettings> configureOptions
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
        Action<IServiceProvider, string?, BlobStorageSettings> configAction
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configAction);
        builder.OptionsBuilder.Configure<IServiceProvider>(
            (settings, sp) => configAction(sp, builder.ServiceKey, settings)
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
    /// Specifies a specific <see cref="BlobClient"/> to use with the Leader Election.
    /// </summary>
    public static ServiceBuilder WithBlobClient(this ServiceBuilder builder, BlobClient blobClient)
    {
        ArgumentNullException.ThrowIfNull(blobClient);
        return builder.WithBlobClientFactory((_, _, _) => Task.FromResult(blobClient));
    }

    /// <summary>
    /// Specifies a BlobClient factory to use with the Leader Election.
    /// </summary>
    public static ServiceBuilder WithBlobClientFactory(
        this ServiceBuilder builder,
        Func<IServiceProvider, BlobStorageSettings, CancellationToken, Task<BlobClient>> factoryFunc
    )
    {
        ArgumentNullException.ThrowIfNull(factoryFunc);
        return builder.WithSettings(
            (sp, _, settings) =>
                settings.BlobClientFactory = (settings, ct) => factoryFunc(sp, settings, ct)
        );
    }

    /// <summary>
    /// Specifies a specific <see cref="BlobContainerClient"/> to use with the Leader Election.
    /// </summary>
    public static ServiceBuilder WithContainerClient(
        this ServiceBuilder builder,
        BlobContainerClient containerClient
    )
    {
        ArgumentNullException.ThrowIfNull(containerClient);
        return builder.WithBlobClientFactory(
            (_, settings, ct) => CreateBlobClient(containerClient, settings, ct)
        );
    }

    /// <summary>
    /// Specifies a <see cref="BlobServiceClient"/> factory to use with the Leader Election.
    /// </summary>
    public static ServiceBuilder WithRegisteredBlobServiceClient(this ServiceBuilder builder)
    {
        return builder.WithBlobClientFactory(
            (sp, settings, ct) => CreateBlobClient(sp, builder.ServiceKey, settings, ct)
        );
    }

    /// <summary>
    /// Specifies a specific <see cref="BlobServiceClient"/> to use with the Leader Election.
    /// </summary>
    public static ServiceBuilder WithBlobServiceClient(
        this ServiceBuilder builder,
        BlobServiceClient blobServiceClient
    )
    {
        ArgumentNullException.ThrowIfNull(blobServiceClient);
        return builder.WithBlobServiceClientFactory(_ => blobServiceClient);
    }

    /// <summary>
    /// Specifies a <see cref="BlobServiceClient"/> factory to use with the Leader Election.
    /// </summary>
    public static ServiceBuilder WithBlobServiceClientFactory(
        this ServiceBuilder builder,
        Func<IServiceProvider, BlobServiceClient> blobServiceClientFactory
    )
    {
        ArgumentNullException.ThrowIfNull(blobServiceClientFactory);
        return builder.WithBlobClientFactory(
            (sp, settings, ct) =>
            {
                var bsc = blobServiceClientFactory(sp);
                return CreateBlobClient(bsc, settings, ct);
            }
        );
    }

    private static Task<BlobClient> CreateBlobClient(
        IServiceProvider sp,
        string? serviceKey,
        BlobStorageSettings settings,
        CancellationToken ct
    )
    {
        var bsc =
            sp.GetKeyedService<BlobServiceClient>(serviceKey)
            ?? sp.GetRequiredService<BlobServiceClient>();
        return CreateBlobClient(bsc, settings, ct);
    }

    internal static Task<BlobClient> CreateBlobClient(
        BlobServiceClient bsc,
        BlobStorageSettings settings,
        CancellationToken ct
    )
    {
        var bcc = bsc.GetBlobContainerClient(settings.ContainerName);
        return CreateBlobClient(bcc, settings, ct);
    }

    private static async Task<BlobClient> CreateBlobClient(
        BlobContainerClient bcc,
        BlobStorageSettings settings,
        CancellationToken ct
    )
    {
        var blobClient = bcc.GetBlobClient(settings.BlobName);

        if (settings.CreateContainerIfNotExists)
        {
            await bcc.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }

        return blobClient;
    }
}
