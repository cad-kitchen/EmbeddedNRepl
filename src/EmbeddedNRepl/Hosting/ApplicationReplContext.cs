using System;

namespace EmbeddedNRepl.Hosting;

/// <summary>Host-owned snapshots for applications without a document or command model.</summary>
public sealed class ApplicationReplContext : IReplHostContext
{
    private readonly object gate = new object();
    private readonly string runtimeGroupId;
    private ReplExecutionContext current;
    private bool active = true;

    public ApplicationReplContext(string runtimeGroupId, object? hostState = null)
    {
        current = new ReplExecutionContext(runtimeGroupId, hostState);
        this.runtimeGroupId = runtimeGroupId;
    }

    /// <summary>Invalidates previous snapshots, even when the state object is unchanged.</summary>
    public void Publish(object? hostState = null)
    {
        lock (gate)
        {
            current = new ReplExecutionContext(runtimeGroupId, hostState);
            active = true;
        }
    }

    public void Invalidate()
    {
        lock (gate)
            active = false;
    }

    public ReplExecutionContext Capture()
    {
        lock (gate)
            return current;
    }

    public bool IsCurrent(ReplExecutionContext captured)
    {
        lock (gate)
            return active && ReferenceEquals(current, captured);
    }
}
