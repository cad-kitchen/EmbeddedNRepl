using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EmbeddedNRepl.Hosting;

/// <summary>One owner-pumped queue for language work from every host entry point.</summary>
public sealed class RuntimeCoordinator : IReplExecutionScheduler, IDisposable
{
    private readonly object gate = new object();
    private readonly LinkedList<Work> queue = new LinkedList<Work>();
    private bool stopping;
    private bool busy;
    private Work? active;
    private RuntimeDispatchPhase phase;
    public bool HasPendingWork
    {
        get
        {
            lock (gate)
                return queue.Count != 0;
        }
    }
    public bool IsBusy
    {
        get
        {
            lock (gate)
                return busy;
        }
    }
    public RuntimeDispatchState GetDispatchState()
    {
        lock (gate)
            return new RuntimeDispatchState(phase, queue.Count, active?.OperationId);
    }
    public IReplScheduledOperation Schedule(Guid operationId, ReplExecutionContext context, Func<CancellationToken, Task> callback)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (callback == null)
            throw new ArgumentNullException(nameof(callback));
        lock (gate)
        {
            if (stopping)
                throw new ObjectDisposedException(nameof(RuntimeCoordinator));
            if (queue.Count >= 128)
                throw new InvalidOperationException("The runtime owner queue is full.");
            var work = new Work(this, operationId, callback);
            work.Node = queue.AddLast(work);
            return work;
        }
    }

    /// <summary>
    /// Called on the owner's UI thread. The vendor bridge must enter its supported context,
    /// invoke the supplied callback exactly once, and await it before leaving that context.
    /// No user exception is allowed to escape through the vendor callback.
    /// </summary>
    public async Task ExecuteNextAsync(Func<Func<Task>, Task> enterHostContext)
    {
        if (enterHostContext == null)
            throw new ArgumentNullException(nameof(enterHostContext));
        Work work;
        lock (gate)
        {
            if (busy || stopping || queue.First == null)
                return;
            busy = true;
            active = work = queue.First.Value;
            phase = RuntimeDispatchPhase.WaitingForHost;
        }
        Exception? failure = null;
        Task? execution = null;
        var invoked = false;
        async Task Invoke()
        {
            Func<CancellationToken, Task> callback;
            lock (gate)
            {
                if (invoked)
                {
                    failure = new InvalidOperationException("Host invoked one lease twice.");
                    return;
                }
                invoked = true;
                if (work.Node == null)
                {
                    // Cancelled while waiting for the vendor context. Its bridge must still return.
                    if (ReferenceEquals(active, work))
                        phase = RuntimeDispatchPhase.WaitingForHostExit;
                    return;
                }
                queue.Remove(work.Node);
                work.Node = null;
                work.State = ReplScheduledPhase.Running;
                phase = RuntimeDispatchPhase.Executing;
                callback = work.Callback!;
            }
            try
            {
                await callback(work.Cancellation.Token);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(active, work))
                        phase = RuntimeDispatchPhase.WaitingForHostExit;
                }
            }
        }
        try
        {
            await enterHostContext(() => execution = Invoke());
        }
        catch (Exception error) { failure = error; }
        // Keep the lease even if a defective bridge returns before its callback finishes.
        if (execution != null)
            await execution;
        lock (gate)
        {
            if (work.State != ReplScheduledPhase.Finished)
            {
                if (work.Node != null)
                {
                    queue.Remove(work.Node);
                    work.Node = null;
                }
                if (!invoked && failure == null)
                    failure = new InvalidOperationException("Host did not invoke its execution callback.");
                work.Finish(failure);
            }
            active = null;
            busy = false;
            phase = RuntimeDispatchPhase.Idle;
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (stopping)
                return;
            stopping = true;
            while (queue.First != null)
                queue.First.Value.TryCancelQueued();
            active?.RequestCancellation();
        }
    }

    private sealed class Work : IReplScheduledOperation
    {
        private readonly RuntimeCoordinator owner;
        public Work(RuntimeCoordinator owner, Guid operationId, Func<CancellationToken, Task> callback)
        {
            this.owner = owner;
            OperationId = operationId;
            Callback = callback;
        }
        public Guid OperationId
        {
            get;
        }
        public Func<CancellationToken, Task>? Callback;
        public LinkedListNode<Work>? Node;
        public ReplScheduledPhase State;
        private bool requested;
        private bool signalling;
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
        private readonly TaskCompletionSource<ReplScheduleOutcome> finished = new TaskCompletionSource<ReplScheduleOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReplScheduledPhase Phase
        {
            get
            {
                lock (owner.gate)
                    return State;
            }
        }
        public bool CancellationRequested
        {
            get
            {
                lock (owner.gate)
                    return requested;
            }
        }
        public Task<ReplScheduleOutcome> Completion => finished.Task;
        public bool TryCancelQueued()
        {
            lock (owner.gate)
            {
                if (Node == null)
                    return false;
                owner.queue.Remove(Node);
                Node = null;
                Callback = null;
                requested = true;
                State = ReplScheduledPhase.Finished;
                if (!signalling)
                    Cancellation.Dispose();
                finished.TrySetResult(ReplScheduleOutcome.CancelledBeforeStart);
                return true;
            }
        }
        public void RequestCancellation()
        {
            lock (owner.gate)
            {
                if (requested || State == ReplScheduledPhase.Finished)
                    return;
                requested = true;
                signalling = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        Cancellation.Cancel();
                    }
                    catch (AggregateException) { /* Isolate arbitrary user cancellation callbacks. */ }
                    finally
                    {
                        lock (owner.gate)
                        {
                            signalling = false;
                            if (State == ReplScheduledPhase.Finished)
                                Cancellation.Dispose();
                        }
                    }
                });
            }
        }
        public void Finish(Exception? failure)
        {
            State = ReplScheduledPhase.Finished;
            Callback = null;
            if (!signalling)
                Cancellation.Dispose();
            if (failure == null)
                finished.TrySetResult(ReplScheduleOutcome.CallbackFinished);
            else
                finished.TrySetException(failure);
        }
    }
}
