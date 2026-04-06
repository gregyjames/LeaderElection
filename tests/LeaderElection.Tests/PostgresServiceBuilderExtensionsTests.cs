using LeaderElection.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeaderElection.Tests;

public class PostgresServiceBuilderExtensionsTests
{
    [Fact]
    public void ShouldRegisterAllRequiredServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresLeaderElection(options =>
        {
            options.ConnectionString = "Host=localhost";
            options.LockId = 12345;
        });

        var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetService<ILeaderElection>().Should().NotBeNull();

        serviceProvider.GetService<PostgresLeaderElection>().Should().NotBeNull();

        var options = serviceProvider.GetService<IOptions<PostgresSettings>>();
        options.Should().NotBeNull();
        options.Value.ConnectionString.Should().Be("Host=localhost");

        var validators = serviceProvider.GetServices<IValidateOptions<PostgresSettings>>();
        var validateOptionsEnumerable = validators.ToList();
        validateOptionsEnumerable.Should().NotBeEmpty();
        validateOptionsEnumerable.Should().ContainSingle(v => v is PostgresSettingsValidator);
    }

    [Fact]
    public void ShouldFailValidationWhenSettingsAreInvalid()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresLeaderElection(options =>
        {
            // Specifically leave ConnectionString empty
            options.ConnectionString = string.Empty;
            options.LockId = 12345;
        });

        var serviceProvider = services.BuildServiceProvider();
        var optionsProvider = serviceProvider.GetRequiredService<
            IOptionsSnapshot<PostgresSettings>
        >();

        // Accessing .Value should trigger validation via IValidateOptions
        var act = new Func<object>(() => _ = optionsProvider.Value);

        act.Should()
            .Throw<OptionsValidationException>()
            .And.Failures.Should()
            .Contain(f => f.Contains("ConnectionString"));
    }

    [Fact]
    public void ShouldRegisterWithPostgresSettingsInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var settings = new PostgresSettings
        {
            ConnectionString = "Host=localhost",
            LockId = 12345,
            InstanceId = "custom-instance",
        };

        services.AddPostgresLeaderElection(settings);

        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<PostgresSettings>>().Value;

        options.ConnectionString.Should().Be("Host=localhost");
        options.InstanceId.Should().Be("custom-instance");
    }
}
