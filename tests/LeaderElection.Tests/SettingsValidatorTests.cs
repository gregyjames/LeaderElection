using LeaderElection.BlobStorage;
using LeaderElection.DistributedCache;
using LeaderElection.FusionCache;
using LeaderElection.Postgres;
using LeaderElection.Redis;
using LeaderElection.S3;
using Microsoft.Extensions.Options;

namespace LeaderElection.Tests;

public partial class SettingsValidatorTests
{
    internal class TestSettings : LeaderElectionSettingsBase { }

    [OptionsValidator]
    internal partial class TestSettingsValidator : IValidateOptions<TestSettings> { }

    [Fact]
    public void BaseSettingsValidatorShouldSucceedWithDefaults()
    {
        var validator = new TestSettingsValidator();
        var settings = new TestSettings();

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BaseSettingsValidatorShouldFailWhenInvalidInstanceId(string? instanceId)
    {
        var validator = new TestSettingsValidator();
        var settings = new TestSettings { InstanceId = instanceId! };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("InstanceId"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void BaseSettingsValidatorShouldFailWhenInvalidRenewInterval(double renewIntervalSeconds)
    {
        var validator = new TestSettingsValidator();
        var settings = new TestSettings
        {
            RenewInterval = TimeSpan.FromSeconds(renewIntervalSeconds),
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("RenewInterval"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void BaseSettingsValidatorShouldFailWhenInvalidRetryInterval(double retryIntervalSeconds)
    {
        var validator = new TestSettingsValidator();
        var settings = new TestSettings
        {
            RetryInterval = TimeSpan.FromSeconds(retryIntervalSeconds),
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("RetryInterval"));
    }

    [Fact]
    public void BaseSettingsValidatorShouldFailWhenInvalidMaxRetryInterval()
    {
        var validator = new TestSettingsValidator();
        var settings = new TestSettings
        {
            RetryInterval = TimeSpan.FromSeconds(5), // valid
            MaxRetryInterval = TimeSpan.FromSeconds(3), // invalid
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("MaxRetryInterval"));
    }

    [Theory]
    [InlineData(1.0 - 0.001)]
    [InlineData(3.0 + 0.001)]
    public void BaseSettingsValidatorShouldFailWhenInvalidRetryBackoffFactor(
        double retryBackoffFactor
    )
    {
        var validator = new TestSettingsValidator();
        var settings = new TestSettings { RetryBackoffFactor = retryBackoffFactor };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("RetryBackoffFactor"));
    }

    [Theory]
    [InlineData(0.0 - 0.001)]
    [InlineData(1.0 + 0.001)]
    public void BaseSettingsValidatorShouldFailWhenInvalidRetryJitter(double retryJitter)
    {
        var validator = new TestSettingsValidator();
        var settings = new TestSettings { RetryJitter = retryJitter };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("RetryJitter"));
    }

    [Fact]
    public void S3SettingsValidatorShouldValidateCorrectly()
    {
        var validator = new S3SettingsValidator();
        var settings = new S3Settings
        {
            BucketName = string.Empty,
            ObjectKey = string.Empty,
            LeaseDuration = TimeSpan.Zero,
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("BucketName"));
        result.Failures.Should().Contain(f => f.Contains("ObjectKey"));
        result.Failures.Should().Contain(f => f.Contains("LeaseDuration"));
    }

    [Fact]
    public void RedisSettingsValidatorShouldValidateCorrectly()
    {
        var validator = new RedisSettingsValidator();
        var settings = new RedisSettings
        {
            LockKey = string.Empty,
            Database = -1,
            LockExpiry = TimeSpan.Zero,
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("LockKey"));
        result.Failures.Should().NotContain(f => f.Contains("Host"));
        result.Failures.Should().Contain(f => f.Contains("Database"));
        result.Failures.Should().Contain(f => f.Contains("LockExpiry"));
    }

    [Fact]
    public void BlobStorageSettingsValidatorShouldValidateCorrectly()
    {
        var validator = new BlobStorageSettingsValidator();
        var settings = new BlobStorageSettings
        {
            BlobName = string.Empty,
            ContainerName = string.Empty,
            LeaseDuration = TimeSpan.FromSeconds(5),
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("BlobName"));
        result.Failures.Should().Contain(f => f.Contains("ContainerName"));
        result.Failures.Should().Contain(f => f.Contains("LeaseDuration"));
    }

    [Fact]
    public void DistributedCacheSettingsValidatorShouldValidateCorrectly()
    {
        var validator = new DistributedCacheSettingsValidator();
        var settings = new DistributedCacheSettings
        {
            LockKey = string.Empty,
            LockExpiry = TimeSpan.FromSeconds(-1),
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("LockKey"));
        result.Failures.Should().Contain(f => f.Contains("LockExpiry"));
    }

    [Fact]
    public void FusionCacheSettingsValidatorShouldValidateCorrectly()
    {
        var validator = new FusionCacheSettingsValidator();
        var settings = new FusionCacheSettings
        {
            LockKey = string.Empty,
            LockExpiry = TimeSpan.FromSeconds(-1),
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("LockKey"));
        result.Failures.Should().Contain(f => f.Contains("LockExpiry"));
    }

    [Fact]
    public void PostgresSettingsValidatorShouldValidateCorrectly()
    {
        var validator = new PostgresSettingsValidator();
        var settings = new PostgresSettings { InstanceId = "", LockId = 0 };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("InstanceId"));
        result.Failures.Should().NotContain(f => f.Contains("LockId"));
    }
}
