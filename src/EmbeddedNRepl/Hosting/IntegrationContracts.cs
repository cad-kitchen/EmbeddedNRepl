using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbeddedNRepl.Hosting;

public enum ReplExecutionMode
{
    Application, CommandOwned
}
public enum ReplEvaluationOutcome
{
    Succeeded, Failed, Interrupted
}
public enum ReplOutputKind
{
    Out, Error, Value
}
public enum ReplScheduledPhase
{
    Queued, Running, Finished
}
public enum ReplScheduleOutcome
{
    CallbackFinished, CancelledBeforeStart
}

/// <summary>Immutable identity captured at admission; the host validates it immediately before execution.</summary>
public sealed class ReplExecutionContext
{
    /// <summary>Creates an application context; only the host interprets hostState.</summary>
    public ReplExecutionContext(string runtimeGroupId, object? hostState = null)
        : this(runtimeGroupId, null, null, null, ReplExecutionMode.Application)
    {
        if (string.IsNullOrWhiteSpace(runtimeGroupId))
            throw new ArgumentException("Runtime identity is required.", nameof(runtimeGroupId));
        HostState = hostState;
    }

    public object? HostState
    {
        get;
    }

    public ReplExecutionContext(string runtimeGroupId, string? projectId, long? projectRevision,
        string? documentId, ReplExecutionMode mode)
    {
        RuntimeGroupId = runtimeGroupId;
        ProjectId = projectId;
        ProjectRevision = projectRevision;
        DocumentId = documentId;
        Mode = mode;
    }
    public string RuntimeGroupId
    {
        get;
    }
    public string? ProjectId
    {
        get;
    }
    public long? ProjectRevision
    {
        get;
    }
    public string? DocumentId
    {
        get;
    }
    public ReplExecutionMode Mode
    {
        get;
    }
}

public sealed class ReplEvaluation
{
    public ReplEvaluation(string source, string? ns = null,
        string? sourceName = null, long line = 1, long column = 1, int valueQuotaBytes = 65536)
        : this(source, ns, sourceName, line, column, valueQuotaBytes, null) { }

    public ReplEvaluation(string source, string? ns, string? sourceName, long line, long column,
        int valueQuotaBytes, ReplPrintOptions? printing)
    {
        Source = source;
        Namespace = ns;
        SourceName = sourceName;
        Line = line;
        Column = column;
        ValueQuotaBytes = valueQuotaBytes;
        Printing = printing;
    }
    public ReplPrintOptions? Printing
    {
        get;
    }
    public string Source
    {
        get;
    }
    public string? Namespace
    {
        get;
    }
    public string? SourceName
    {
        get;
    }
    public long Line
    {
        get;
    }
    public long Column
    {
        get;
    }
    public int ValueQuotaBytes
    {
        get;
    }
}

/// <summary>Request-local options for the built-in ClojureCLR pretty printer.</summary>
public sealed class ReplPrintOptions
{
    public ReplPrintOptions(int rightMargin = 80, int? length = null, int? level = null)
    {
        if (rightMargin < 1 || length < 0 || level < 0)
            throw new ArgumentOutOfRangeException(nameof(rightMargin));
        RightMargin = Math.Min(rightMargin, 1024);
        Length = length.HasValue ? Math.Min(length.Value, 100) : (int?)null;
        Level = level.HasValue ? Math.Min(level.Value, 10) : (int?)null;
    }
    public int RightMargin
    {
        get;
    }
    public int? Length
    {
        get;
    }
    public int? Level
    {
        get;
    }
}

/// <summary>Opaque language state. Only the language adapter interprets its contents.</summary>
public interface IReplSessionState
{
}
/// <summary>An immutable published binding snapshot; referenced language objects may themselves be mutable.</summary>
public interface IReplSessionSnapshot
{
}

/// <summary>
/// These operations must be bounded and must not execute user code or wait for the owner queue.
/// The adapter initializes its runtime before attachment. Snapshot captures an immutable binding
/// container while the caller still owns evaluation's lease; only completed leases are published.
/// </summary>
public interface IReplSessionStateFactory
{
    /// <summary>On failure the factory cleans up anything it has not returned to the caller.</summary>
    IReplSessionState Create();
    IReplSessionSnapshot Snapshot(IReplSessionState completedState);
    IReplSessionState Clone(IReplSessionSnapshot publishedSnapshot);
    /// <summary>
    /// Called only after all execution using this state has actually finished.
    /// Throwing leaves ownership with the caller; a later shutdown may retry this call.
    /// </summary>
    void Release(IReplSessionState state);
}

public interface IReplOutput
{
    /// <summary>Nonblocking admission of a text slice. False means the slice was not accepted.</summary>
    bool TryWrite(ReplOutputKind kind, string text, int offset, int count);
    void Flush();
}

public interface IReplEvaluator
{
    /// <summary>Completion means execution, printing, binding cleanup, and request output have actually ended.</summary>
    Task<ReplEvaluationOutcome> EvaluateAsync(ReplEvaluation evaluation, IReplSessionState state,
        ReplExecutionContext context, IReplOutput output, CancellationToken cancellationToken);
}

public interface IReplScheduledOperation
{
    ReplScheduledPhase Phase
    {
        get;
    }
    bool CancellationRequested
    {
        get;
    }
    /// <summary>True only when cancellation atomically removed queued work; that callback will never run.</summary>
    bool TryCancelQueued();
    /// <summary>A nonblocking signal, never a claim that a running callback has exited. Do not run user cancellation handlers inline.</summary>
    void RequestCancellation();
    /// <summary>Cannot finish before the callback and its host execution lease have actually ended.</summary>
    Task<ReplScheduleOutcome> Completion
    {
        get;
    }
}

public interface IReplExecutionScheduler
{
    /// <summary>
    /// Nonblocking admission to the owner's shared runtime queue, also used by UI commands and reload.
    /// Do not execute the callback inline on the admitting thread. The callback includes evaluation and printing.
    /// </summary>
    IReplScheduledOperation Schedule(Guid operationId, ReplExecutionContext context,
        Func<CancellationToken, Task> callback);
}

public interface IReplHostContext
{
    ReplExecutionContext Capture();
    bool IsCurrent(ReplExecutionContext captured);
}
