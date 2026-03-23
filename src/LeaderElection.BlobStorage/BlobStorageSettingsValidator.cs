using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LeaderElection.BlobStorage;

[OptionsValidator]
public partial class BlobStorageSettingsValidator : IValidateOptions<BlobStorageSettings>
{
    public static ValidationResult? IsValidLeaseDuration(TimeSpan leaseDuration) =>
        leaseDuration.TotalSeconds is -1 or >= 15 and <= 60
            ? ValidationResult.Success
            : new ValidationResult(
                $"{nameof(BlobStorageSettings.LeaseDuration)} must be between 15 and 60 seconds, or -1 for infinite."
            );
}
