using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LeaderElection.DistributedCache;

[OptionsValidator]
public partial class DistributedCacheSettingsValidator : IValidateOptions<DistributedCacheSettings>
{
    public static ValidationResult? ValidateLockExpiry(TimeSpan lockExpiry) =>
        lockExpiry > TimeSpan.Zero
            ? ValidationResult.Success
            : new ValidationResult(
                $"{nameof(DistributedCacheSettings.LockExpiry)} must be positive."
            );
}
