using Microsoft.Extensions.Options;

namespace LeaderElection.FusionCache;

public class FusionCacheSettingsValidator: BaseSettingsValidator<FusionCacheSettings>, IValidateOptions<FusionCacheSettings>
{
    public ValidateOptionsResult Validate(string? name, FusionCacheSettings options)
    {
        List<string> failures = [..ValidateBase(options)];

        if (string.IsNullOrEmpty(options.LockKey))
        {
            failures.Add($"{nameof(options.LockKey)} cannot be null or empty.");
        }

        if (options.LockExpiry < TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.LockExpiry)} cannot be negative.");
        }
        
        return failures.Count != 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}