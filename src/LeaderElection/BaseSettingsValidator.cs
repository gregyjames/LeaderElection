using System.ComponentModel.DataAnnotations;

namespace LeaderElection;

public static class BaseSettingsValidator
{
    public static ValidationResult? ValidateRenewInterval(TimeSpan renewInterval) =>
        renewInterval > TimeSpan.Zero
            ? ValidationResult.Success
            : new ValidationResult(
                $"{nameof(LeaderElectionSettingsBase.RenewInterval)} must be positive."
            );

    public static ValidationResult? ValidateRetryInterval(TimeSpan retryInterval) =>
        retryInterval > TimeSpan.Zero
            ? ValidationResult.Success
            : new ValidationResult(
                $"{nameof(LeaderElectionSettingsBase.RetryInterval)} must be positive."
            );
}
