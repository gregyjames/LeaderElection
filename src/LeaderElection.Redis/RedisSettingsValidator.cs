using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LeaderElection.Redis;

[OptionsValidator]
public partial class RedisSettingsValidator : IValidateOptions<RedisSettings>
{
    public static ValidationResult? ValidateLockExpiry(TimeSpan lockExpiry) =>
        lockExpiry > TimeSpan.Zero
            ? ValidationResult.Success
            : new ValidationResult($"{nameof(RedisSettings.LockExpiry)} must be positive.");
}
