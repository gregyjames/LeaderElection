using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LeaderElection.S3;

[OptionsValidator]
public partial class S3SettingsValidator : IValidateOptions<S3Settings>
{
    public static ValidationResult? ValidateLeaseDuration(TimeSpan leaseDuration) =>
        leaseDuration > TimeSpan.Zero
            ? ValidationResult.Success
            : new ValidationResult($"{nameof(S3Settings.LeaseDuration)} must be positive.");
}
