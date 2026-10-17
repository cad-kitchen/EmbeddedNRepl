namespace EmbeddedNRepl;

public sealed class ReplStopResult
{
    internal ReplStopResult(bool isComplete)
    {
        IsComplete = isComplete;
    }
    public bool IsComplete
    {
        get;
    }
    public string Status => IsComplete ? "stopped" : "stop-incomplete";
}
