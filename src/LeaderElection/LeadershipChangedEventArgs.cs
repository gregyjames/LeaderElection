namespace LeaderElection;

public class LeadershipChangedEventArgs(bool isLeader) : EventArgs
{
    public bool IsLeader { get; } = isLeader;
}