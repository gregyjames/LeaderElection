using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LeaderElection.FusionCache;

[OptionsValidator]
public partial class FusionCacheSettingsValidator : IValidateOptions<FusionCacheSettings>
{
    public static ValidationResult? ValidateLockExpiry(TimeSpan lockExpiry) =>
        lockExpiry > TimeSpan.Zero
            ? ValidationResult.Success
            : new ValidationResult($"{nameof(FusionCacheSettings.LockExpiry)} must be positive.");
}
