using Microsoft.Extensions.Options;

namespace LeaderElection.Postgres;

//todo: refactor out base settings validation and add for other implementations
public sealed class PostgresSettingsValidator: IValidateOptions<PostgresSettings>
{
    public ValidateOptionsResult Validate(string? name, PostgresSettings options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add("ConnectionString cannot be null or empty.");
        }

        if (string.IsNullOrWhiteSpace(options.InstanceId))
        {
            failures.Add("InstanceId cannot be null or empty.");
        }

        if (options.RetryInterval <= TimeSpan.Zero)
        {
            failures.Add($"RetryInterval must be positive. Got {options.RetryInterval}.");
        }
        
        return failures.Any() ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}