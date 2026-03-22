using System.ComponentModel.DataAnnotations;

namespace LeaderElection.Redis;

public class RedisSettings: LeaderElectionSettingsBase
{
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6379;
    public string Password { get; set; } = string.Empty;
    [Range(0, int.MaxValue)]
    public int Database { get; set; } = 0;
    [Required(AllowEmptyStrings = false)]
    public string LockKey { get; set; } = "leader-election-lock";
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);
}