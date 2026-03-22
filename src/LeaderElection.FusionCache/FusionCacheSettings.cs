using System.ComponentModel.DataAnnotations;

namespace LeaderElection.FusionCache;

public class FusionCacheSettings: LeaderElectionSettingsBase
{
    [Required(AllowEmptyStrings = false)]
    public string LockKey { get; set; } = "leader-election-lock";
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);
}


