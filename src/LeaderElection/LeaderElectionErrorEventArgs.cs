namespace LeaderElection;

public class LeaderElectionErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}