using System;

namespace EmbeddedNRepl.Hosting;

public enum RuntimeDispatchPhase
{
    Idle,
    WaitingForHost,
    Executing,
    WaitingForHostExit
}

/// <summary>A consistent diagnostic snapshot; contains no evaluation text or values.</summary>
public sealed class RuntimeDispatchState
{
    internal RuntimeDispatchState(RuntimeDispatchPhase phase, int queued, Guid? operationId)
    {
        Phase = phase;
        Queued = queued;
        OperationId = operationId;
    }
    public RuntimeDispatchPhase Phase
    {
        get;
    }
    /// <summary>Includes a reserved operation until its callback starts or it is cancelled.</summary>
    public int Queued
    {
        get;
    }
    public Guid? OperationId
    {
        get;
    }
}
