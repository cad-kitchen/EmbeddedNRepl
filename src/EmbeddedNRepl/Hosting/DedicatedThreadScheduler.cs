using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl.Hosting;

public sealed class DedicatedThreadScheduler : IReplExecutionScheduler, IDisposable
{
    private readonly object gate = new object();
    private readonly LinkedList<Work> queue = new LinkedList<Work>();
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly TaskCompletionSource<bool> finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopping;
    public DedicatedThreadScheduler(Action initialize)
    {
        if (initialize == null)
            throw new ArgumentNullException(nameof(initialize));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                initialize();
                ready.TrySetResult(true);
                Run();
            }
            catch (Exception error) { ready.TrySetException(error); finished.TrySetException(error); }
            finally { lock (gate) { stopping = true; wake.Dispose(); } finished.TrySetResult(true); }
        })
        {
            IsBackground = true,
            Name = "EmbeddedNRepl standalone Clojure owner"
        };
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }
    public Task Completion => finished.Task;
    public IReplScheduledOperation Schedule(Guid operationId, ReplExecutionContext context, Func<CancellationToken, Task> callback)
    {
        if (callback == null)
            throw new ArgumentNullException(nameof(callback));
        lock (gate)
        {
            if (stopping)
                throw new ObjectDisposedException(nameof(DedicatedThreadScheduler));
            if (queue.Count >= 128)
                throw new InvalidOperationException("Standalone owner queue is full.");
            var work = new Work(this, callback);
            work.Node = queue.AddLast(work);
            wake.Set();
            return work;
        }
    }
    private void Run()
    {
        while (true)
        {
            Work? work = null;
            lock (gate)
            {
                if (queue.First != null)
                {
                    work = queue.First.Value;
                    queue.RemoveFirst();
                    work.Node = null;
                    work.State = ReplScheduledPhase.Running;
                }
                else if (stopping)
                    return;
            }
            if (work == null)
            {
                wake.WaitOne();
                continue;
            }
            Exception? failure = null;
            try
            {
                work.Callback!(work.Cancellation.Token).GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            lock (gate)
            {
                work.State = ReplScheduledPhase.Finished;
                work.Callback = null;
                if (!work.Signalling)
                    work.Cancellation.Dispose();
                if (failure == null)
                    work.Source.TrySetResult(ReplScheduleOutcome.CallbackFinished);
                else
                    work.Source.TrySetException(failure);
            }
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
            wake.Set();
        }
    }
    private sealed class Work : IReplScheduledOperation
    {
        private readonly DedicatedThreadScheduler owner;
        public Work(DedicatedThreadScheduler owner, Func<CancellationToken, Task> callback)
        {
            this.owner = owner;
            Callback = callback;
        }
        public Func<CancellationToken, Task>? Callback;
        public LinkedListNode<Work>? Node;
        public ReplScheduledPhase State;
        public bool Signalling;
        private bool requested;
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
        public bool CancellationRequested
        {
            get
            {
                lock (owner.gate)
                    return requested;
            }
        }
        public Task<ReplScheduleOutcome> Completion => Source.Task;
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
                Cancellation.Dispose();
                Source.TrySetResult(ReplScheduleOutcome.CancelledBeforeStart);
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
                Signalling = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        Cancellation.Cancel();
                    }
                    catch (AggregateException) { /* A user cancellation handler cannot break the owner loop. */ }
                    finally
                    {
                        lock (owner.gate)
                        {
                            Signalling = false;
                            if (State == ReplScheduledPhase.Finished)
                                Cancellation.Dispose();
                        }
                    }
                });
            }
        }
    }
}
