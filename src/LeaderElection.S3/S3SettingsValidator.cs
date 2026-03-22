using Microsoft.Extensions.Options;

namespace LeaderElection.S3;

public class S3SettingsValidator: BaseSettingsValidator<S3Settings>, IValidateOptions<S3Settings>
{
    public ValidateOptionsResult Validate(string? name, S3Settings options)
    {
        List<string> failures = [..ValidateBase(options)];

        if (string.IsNullOrEmpty(options.BucketName))
        {
            failures.Add($"{nameof(options.BucketName)} is required.");
        }
        
        if (string.IsNullOrWhiteSpace(options.ObjectKey)){
            failures.Add($"{nameof(options.ObjectKey)} cannot be null or empty");
        }
        
        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.LeaseDuration)} must be greater than zero.");
        }
        
        return failures.Count != 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}