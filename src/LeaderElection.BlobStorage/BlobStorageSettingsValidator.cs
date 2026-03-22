using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LeaderElection.BlobStorage;

public class BlobStorageSettingsValidator: BaseSettingsValidator<BlobStorageSettings>, IValidateOptions<BlobStorageSettings>
{
    public ValidateOptionsResult Validate(string? name, BlobStorageSettings options)
    {
        List<string> failures = [..ValidateBase(options)];
        
        if (string.IsNullOrWhiteSpace(options.BlobName))
        {
            failures.Add($"{nameof(options.BlobName)} cannot be null or empty.");
        }

        if (string.IsNullOrWhiteSpace(options.ContainerName))
        {
            failures.Add($"{nameof(options.ContainerName)} cannot be null or empty.");
        }

        ValidateLeaseDuration(options, ref failures);

        return failures.Count != 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
    
    private static void ValidateLeaseDuration(BlobStorageSettings options, ref List<string> failures)
    {
        double seconds = options.LeaseDuration.TotalSeconds;
        
        bool isValid = seconds is -1 or >= 15 and <= 60;

        if (!isValid)
        {
            failures.Add($"{nameof(options.LeaseDuration)} must be between 15 and 60 seconds, or -1 for infinite.");
        }
    }
}