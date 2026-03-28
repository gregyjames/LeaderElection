using LeaderElection.Postgres;

namespace LeaderElection.Tests;

public class PostgresSettingsValidatorTests
{
    private readonly PostgresSettingsValidator _validator = new();

    [Fact]
    public void ShouldSucceedWhenSettingsAreValid()
    {
        var settings = new PostgresSettings
        {
            ConnectionString = "Host=localhost;Database=test",
            InstanceId = "test-instance",
            LockId = 12345,
            RetryInterval = TimeSpan.FromSeconds(5)
        };

        var result = _validator.Validate(null, settings);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ShouldFailWhenConnectionStringIsInvalid(string? connectionString)
    {
        var settings = new PostgresSettings
        {
            ConnectionString = connectionString!,
            InstanceId = "test-instance",
            LockId = 12345
        };

        var result = _validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("ConnectionString"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ShouldFailWhenInstanceIdIsInvalid(string? instanceId)
    {
        var settings = new PostgresSettings
        {
            ConnectionString = "Host=localhost",
            InstanceId = instanceId!,
            LockId = 12345
        };

        var result = _validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("InstanceId"));
    }

    [Fact]
    public void ShouldFailWhenRetryIntervalIsNegative()
    {
        var settings = new PostgresSettings
        {
            ConnectionString = "Host=localhost",
            InstanceId = "test-instance",
            LockId = 12345,
            RetryInterval = TimeSpan.FromSeconds(-1)
        };

        var result = _validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("RetryInterval"));
    }
}
