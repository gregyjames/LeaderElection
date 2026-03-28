namespace LeaderElection;

public class LeadershipExceptionEventArgs(Exception ex) : EventArgs
{
    public Exception LeadershipException { get; set; } = ex;
}
