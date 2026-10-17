using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl.Testing;

public sealed class OperationRecorder
{
    private readonly ConcurrentQueue<string> events = new ConcurrentQueue<string>();
    public void Record(string value) => events.Enqueue(value);
    public string[] Snapshot() => events.ToArray();
}

public sealed class EvaluationBarrier : IDisposable
{
    private readonly TaskCompletionSource<bool> entered = NewSignal();
    private readonly TaskCompletionSource<bool> release = NewSignal();
    private readonly TaskCompletionSource<bool> cancelled = NewSignal();
    public Task Entered => TestDeadline.Complete(entered.Task);
    public Task Cancelled => TestDeadline.Complete(cancelled.Task);
    public void Release() => release.TrySetResult(true);
    public void Dispose() => Release();
    public async Task<ReplEvaluationOutcome> Evaluate(CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return ReplEvaluationOutcome.Succeeded;
        }
    }
    internal static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class FakeState : IReplSessionState
{
    public int Value;
    public bool Released;
}
public sealed class FakeSnapshot : IReplSessionSnapshot
{
    public FakeSnapshot(int value)
    {
        Value = value;
    }
    public int Value
    {
        get;
    }
}
public sealed class FakeStateFactory : IReplSessionStateFactory
{
    private int active;
    public Action<string> Before { get; set; } = operation => { };
    public int Active => Volatile.Read(ref active);
    public IReplSessionState Create()
    {
        Before("create");
        Interlocked.Increment(ref active);
        return new FakeState();
    }
    public IReplSessionSnapshot Snapshot(IReplSessionState state)
    {
        Before("snapshot");
        return new FakeSnapshot(((FakeState)state).Value);
    }
    public IReplSessionState Clone(IReplSessionSnapshot snapshot)
    {
        Before("clone");
        var state = (FakeState)Create();
        state.Value = ((FakeSnapshot)snapshot).Value;
        return state;
    }
    public void Release(IReplSessionState state)
    {
        Before("release");
        var value = (FakeState)state;
        if (value.Released)
            throw new InvalidOperationException("State was released twice.");
        value.Released = true;
        Interlocked.Decrement(ref active);
    }
}
public sealed class FakeHost : IReplHostContext
{
    public ReplExecutionContext Current { get; set; } = new ReplExecutionContext("test-runtime", null, null, null, ReplExecutionMode.Application);
    public OperationRecorder Events { get; } = new OperationRecorder();
    public ReplExecutionContext Capture() => Current;
    public bool IsCurrent(ReplExecutionContext captured) => ReferenceEquals(captured, Current);
}
public sealed class FakeEvaluator : IReplEvaluator
{
    private int active;
    public int Active => Volatile.Read(ref active);
    public ConcurrentQueue<ReplEvaluation> Calls { get; } = new ConcurrentQueue<ReplEvaluation>();
    public Func<ReplEvaluation, IReplSessionState, ReplExecutionContext, IReplOutput, CancellationToken, Task<ReplEvaluationOutcome>> Behavior
    {
        get; set;
    }
        = (request, state, context, output, cancel) => Task.FromResult(ReplEvaluationOutcome.Succeeded);
    public async Task<ReplEvaluationOutcome> EvaluateAsync(ReplEvaluation request, IReplSessionState state,
        ReplExecutionContext context, IReplOutput output, CancellationToken cancellationToken)
    {
        Calls.Enqueue(request);
        Interlocked.Increment(ref active);
        try
        {
            return await Behavior(request, state, context, output, cancellationToken).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref active); }
    }
}

public sealed class ControlledScheduler : IReplExecutionScheduler
{
    private readonly object gate = new object();
    private readonly LinkedList<Work> queue = new LinkedList<Work>();
    private int active;
    public OperationRecorder Events { get; } = new OperationRecorder();
    public Func<Task> AfterCallback { get; set; } = () => Task.CompletedTask;
    public int Active
    {
        get
        {
            lock (gate)
                return active;
        }
    }
    public int Queued
    {
        get
        {
            lock (gate)
                return queue.Count;
        }
    }
    public IReplScheduledOperation Schedule(Guid operationId, ReplExecutionContext context, Func<CancellationToken, Task> callback)
    {
        lock (gate)
        {
            var work = new Work(this, operationId, callback);
            work.Node = queue.AddLast(work);
            Events.Record("queued:" + operationId);
            return work;
        }
    }
    public async Task RunNextAsync()
    {
        Work work;
        lock (gate)
        {
            if (active != 0)
                throw new InvalidOperationException("The execution lease is occupied.");
            if (queue.First == null)
                throw new InvalidOperationException("No work is queued.");
            work = queue.First.Value;
            queue.RemoveFirst();
            work.Node = null;
            work.State = ReplScheduledPhase.Running;
            active++;
            Events.Record("started:" + work.Id);
        }
        Exception? failure = null;
        try
        {
            await work.Callback(work.Cancellation.Token).ConfigureAwait(false);
            await AfterCallback().ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        lock (gate)
        {
            work.State = ReplScheduledPhase.Finished;
            active--;
            Events.Record("finished:" + work.Id);
            if (failure == null)
                work.Source.TrySetResult(ReplScheduleOutcome.CallbackFinished);
            else
                work.Source.TrySetException(failure);
        }
    }
    private sealed class Work : IReplScheduledOperation
    {
        private readonly ControlledScheduler owner;
        public Work(ControlledScheduler owner, Guid id, Func<CancellationToken, Task> callback)
        {
            this.owner = owner;
            Id = id;
            Callback = callback;
        }
        public Guid Id
        {
            get;
        }
        public Func<CancellationToken, Task> Callback
        {
            get;
        }
        public LinkedListNode<Work>? Node;
        public ReplScheduledPhase State = ReplScheduledPhase.Queued;
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
        public TaskCompletionSource<ReplScheduleOutcome> Source { get; } = new TaskCompletionSource<ReplScheduleOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReplScheduledPhase Phase
        {
            get
            {
                lock (owner.gate)
                    return State;
            }
        }
        public bool CancellationRequested => Cancellation.IsCancellationRequested;
        public Task<ReplScheduleOutcome> Completion => Source.Task;
        public bool TryCancelQueued()
        {
            lock (owner.gate)
            {
                if (Node == null)
                    return false;
                owner.queue.Remove(Node);
                Node = null;
                State = ReplScheduledPhase.Finished;
                owner.Events.Record("cancelled-before-start:" + Id);
                Source.TrySetResult(ReplScheduleOutcome.CancelledBeforeStart);
                return true;
            }
        }
        public void RequestCancellation() => Cancellation.Cancel();
    }
}
