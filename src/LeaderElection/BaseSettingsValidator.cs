using Microsoft.Extensions.Options;

namespace LeaderElection;

public class BaseSettingsValidator<T> where T : LeaderElectionSettingsBase
{
    protected IEnumerable<string> ValidateBase(T settings)
    {
        if (string.IsNullOrEmpty(settings.InstanceId))
        {
            yield return $"{nameof(settings.InstanceId)} cannot be null or empty.";
        }
        
        if (settings.RenewInterval <= TimeSpan.Zero)
        {
            yield return $"{nameof(settings.RenewInterval)} must be positive";
        }

        if (settings.RetryInterval <= TimeSpan.Zero)
        {
            yield return $"{nameof(settings.RetryInterval)} must be positive";
        }

        if (settings.MaxRetryAttempts < 0)
        {
            yield return $"{nameof(settings.MaxRetryAttempts)} cannot be negative";
        }
    }
}