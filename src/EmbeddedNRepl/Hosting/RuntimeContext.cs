using System;

namespace EmbeddedNRepl.Hosting;

/// <summary>Immutable context published by the UI owner; transport threads never inspect CAD objects.</summary>
public sealed class RuntimeContext : IReplHostContext
{
    private readonly object gate = new object();
    private readonly string runtimeGroupId;
    private ReplExecutionContext current;
    public RuntimeContext(string runtimeGroupId)
    {
        if (string.IsNullOrWhiteSpace(runtimeGroupId))
            throw new ArgumentException("Runtime identity is required.", nameof(runtimeGroupId));
        this.runtimeGroupId = runtimeGroupId;
        current = new ReplExecutionContext(runtimeGroupId, null, null, null, ReplExecutionMode.CommandOwned);
    }
    public void Publish(string? documentId, string? projectId = null, long? projectRevision = null)
    {
        lock (gate)
            current = new ReplExecutionContext(runtimeGroupId, projectId, projectRevision, documentId, ReplExecutionMode.CommandOwned);
    }
    public ReplExecutionContext Capture()
    {
        lock (gate)
            return current;
    }
    public bool IsCurrent(ReplExecutionContext captured)
    {
        lock (gate)
            return ReferenceEquals(current, captured) && current.DocumentId != null;
    }
}
