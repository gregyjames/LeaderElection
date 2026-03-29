namespace LeaderElection;

public sealed class LeadershipChangedEventArgs(bool isLeader) : EventArgs
{
    public bool IsLeader { get; } = isLeader;
}
