namespace LeaderElection;

public class LeadershipChangedEventArgs(bool isLeader): EventArgs
{
    public bool LeadershipChanged { get; set; } = isLeader;
}
