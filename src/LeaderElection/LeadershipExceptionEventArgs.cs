namespace LeaderElection;

public sealed class LeadershipExceptionEventArgs(Exception ex) : EventArgs
{
    public Exception LeadershipException { get; } = ex;
}
