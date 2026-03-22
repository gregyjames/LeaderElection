using LeaderElection.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeaderElection.Tests;

public class PostgresServiceBuilderExtensionsTests
{
    [Fact]
    public void Should_Register_All_Required_Services()
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
        options!.Value.ConnectionString.Should().Be("Host=localhost");
        
        var validator = serviceProvider.GetService<IValidateOptions<PostgresSettings>>();
        validator.Should().NotBeNull();
        validator.Should().BeOfType<PostgresSettingsValidator>();
    }

    [Fact]
    public void Should_Fail_Validation_When_Settings_Are_Invalid()
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
        var optionsProvider = serviceProvider.GetRequiredService<IOptionsSnapshot<PostgresSettings>>();

        // Accessing .Value should trigger validation via IValidateOptions
        var act = new Func<object>(() => _ = optionsProvider.Value);

        act.Should().Throw<OptionsValidationException>().And.Failures.Should().Contain(f => f.Contains("ConnectionString"));
    }

    [Fact]
    public void Should_Register_With_PostgresSettings_Instance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        
        var settings = new PostgresSettings
        {
            ConnectionString = "Host=localhost",
            LockId = 12345,
            InstanceId = "custom-instance"
        };

        services.AddPostgresLeaderElection(settings);

        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<PostgresSettings>>().Value;

        options.ConnectionString.Should().Be("Host=localhost");
        options.InstanceId.Should().Be("custom-instance");
    }
}
