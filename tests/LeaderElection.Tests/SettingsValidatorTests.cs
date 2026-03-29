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
    public void BaseSettingsValidatorShouldFailWhenDefaultSettings()
    {
        var validator = new TestSettingsValidator();
        var settings = new TestSettings { InstanceId = string.Empty, RenewInterval = TimeSpan.Zero, RetryInterval = TimeSpan.Zero };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("InstanceId"));
        result.Failures.Should().Contain(f => f.Contains("RenewInterval"));
        result.Failures.Should().Contain(f => f.Contains("RetryInterval"));
    }

    [Fact]
    public void S3SettingsValidatorShouldValidateCorrectly()
    {
        var validator = new S3SettingsValidator();
        var settings = new S3Settings
        {
            BucketName = string.Empty,
            ObjectKey = string.Empty,
            LeaseDuration = TimeSpan.Zero
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
            Host = string.Empty,
            Database = -1,
            LockExpiry = TimeSpan.Zero
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("LockKey"));
        result.Failures.Should().Contain(f => f.Contains("Host"));
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
            LeaseDuration = TimeSpan.FromSeconds(5)
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
            LockExpiry = TimeSpan.FromSeconds(-1)
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
            LockExpiry = TimeSpan.FromSeconds(-1)
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
        var settings = new PostgresSettings
        {
            ConnectionString = string.Empty,
            LockId = 0
        };

        var result = validator.Validate(null, settings);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("ConnectionString"));
        result.Failures.Should().NotContain(f => f.Contains("LockId"));
    }
}
