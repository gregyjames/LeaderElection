using Microsoft.Extensions.Options;

namespace LeaderElection.Redis;

public class RedisSettingsValidator: BaseSettingsValidator<RedisSettings>, IValidateOptions<RedisSettings>
{
    public ValidateOptionsResult Validate(string? name, RedisSettings options)
    {
        List<string> failures = [..ValidateBase(options)];

        if (string.IsNullOrEmpty(options.LockKey))
        {
            failures.Add("LockKey cannot be null or empty.");
        }

        if (string.IsNullOrEmpty(options.Host))
        {
            failures.Add("Host cannot be null or empty.");
        }

        if (options.Database < 0)
        {
            failures.Add("Database number must be positive.");
        }

        if (options.LockExpiry <= TimeSpan.Zero)
        {
            failures.Add("LockExpiry must be positive.");
        }
        
        return failures.Count != 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}