using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl.Testing;

public sealed class ManualClock : IReplClock
{
    private sealed class Waiter
    {
        public TimeSpan Due;
        public readonly TaskCompletionSource<bool> Source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object gate = new object();
    private readonly List<Waiter> waiters = new List<Waiter>();
    private readonly SemaphoreSlim scheduled = new SemaphoreSlim(0);
    private TimeSpan elapsed;
    public TimeSpan Elapsed
    {
        get
        {
            lock (gate)
                return elapsed;
        }
    }
    public int PendingDelays
    {
        get
        {
            lock (gate)
                return waiters.Count;
        }
    }
    public Task WaitForDelay() => TestDeadline.Complete(scheduled.WaitAsync());
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (gate)
        {
            waiter = new Waiter { Due = elapsed + delay };
            waiters.Add(waiter);
        }
        var registration = cancellationToken.Register(() =>
        {
            lock (gate)
                waiters.Remove(waiter);
            waiter.Source.TrySetCanceled();
        });
        scheduled.Release();
        return Finish(waiter.Source.Task, registration);
    }
    private static async Task Finish(Task task, CancellationTokenRegistration registration)
    {
        using (registration)
            await task.ConfigureAwait(false);
    }
    public void Advance(TimeSpan duration)
    {
        Waiter[] ready;
        lock (gate)
        {
            elapsed += duration;
            ready = waiters.Where(x => x.Due <= elapsed).ToArray();
            foreach (var waiter in ready)
                waiters.Remove(waiter);
        }
        foreach (var waiter in ready)
            waiter.Source.TrySetResult(true);
    }
}
