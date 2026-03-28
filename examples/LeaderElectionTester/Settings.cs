using System.Diagnostics.CodeAnalysis;
using LeaderElection.Redis;

namespace LeaderElectionTester;

#pragma warning disable CA1812
internal sealed class Settings
{
    public RedisSettings Redis { get; init; } = new();
}
