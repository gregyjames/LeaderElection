using LeaderElection.Postgres;

namespace LeaderElection.Tests;

public class PostgresSettingsValidatorTests
{
    private readonly PostgresSettingsValidator _validator = new();

    [Fact]
    public void ShouldSucceedWhenDefaultSettings()
    {
        var settings = new PostgresSettings();

        var result = _validator.Validate(null, settings);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void ShouldSucceedWhenSettingsAreValid()
    {
        var settings = new PostgresSettings
        {
            ConnectionString = "Host=localhost;Database=test",
            ConnectionFactory = null,
            InstanceId = "test-instance",
            LockId = 12345,
            RetryInterval = TimeSpan.FromSeconds(5),
        };

        var result = _validator.Validate(null, settings);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bogus")]
    public void ShouldSucceedWhenConnectionStringIsAnything(string? connectionString)
    {
        // This succeeds because a) the ConnectionFactory is an alternative to
        // ConnectionString, and b) we do not validate connection string syntax.
        var settings = new PostgresSettings { ConnectionString = connectionString };

        var result = _validator.Validate(null, settings);

        result.Failed.Should().BeFalse();
    }

    [Fact]
    public void ShouldSucceedWhenConnectionFactoryIsNull()
    {
        var settings = new PostgresSettings { ConnectionFactory = null };
        var result = _validator.Validate(null, settings);
        result.Failed.Should().BeFalse();
    }

    [Fact]
    public void ShouldSucceedWhenConnectionFactoryIsNonNull()
    {
        var settings = new PostgresSettings { ConnectionFactory = _ => null! };
        var result = _validator.Validate(null, settings);
        result.Failed.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ShouldFailWhenInstanceIdIsInvalid(string? instanceId)
    {
        var settings = new PostgresSettings { InstanceId = instanceId! };

        var result = _validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("InstanceId"));
    }

    [Fact]
    public void ShouldFailWhenRetryIntervalIsNegative()
    {
        var settings = new PostgresSettings { RetryInterval = TimeSpan.FromSeconds(-1) };

        var result = _validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("RetryInterval"));
    }
}
