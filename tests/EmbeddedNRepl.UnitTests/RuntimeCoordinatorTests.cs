using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Testing;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class RuntimeCoordinatorTests
{
    private static readonly ReplExecutionContext Context = new ReplExecutionContext("owner", null, null, "drawing", ReplExecutionMode.CommandOwned);
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task DispatchSnapshotDistinguishesHostEntryExecutionAndHostExit()
    {
        using var owner = new RuntimeCoordinator();
        var enter = Signal();
        var started = Signal();
        var release = Signal();
        var returned = Signal();
        var exit = Signal();
        var operationId = Guid.NewGuid();
        owner.Schedule(operationId, Context, async _ => { started.SetResult(true); await release.Task; });
        var pump = owner.ExecuteNextAsync(async invoke =>
        {
            await enter.Task;
            await invoke();
            returned.SetResult(true);
            await exit.Task;
        });
        try
        {
            var waiting = owner.GetDispatchState();
            Assert.That(waiting.Phase, Is.EqualTo(RuntimeDispatchPhase.WaitingForHost));
            Assert.That(waiting.Queued, Is.EqualTo(1));
            Assert.That(waiting.OperationId, Is.EqualTo(operationId));
            enter.TrySetResult(true);
            await TestDeadline.Complete(started.Task);
            var running = owner.GetDispatchState();
            Assert.That(running.Phase, Is.EqualTo(RuntimeDispatchPhase.Executing));
            Assert.That(running.Queued, Is.Zero);
            Assert.That(running.OperationId, Is.EqualTo(operationId));
            release.TrySetResult(true);
            await TestDeadline.Complete(returned.Task);
            var cleanup = owner.GetDispatchState();
            Assert.That(cleanup.Phase, Is.EqualTo(RuntimeDispatchPhase.WaitingForHostExit));
            Assert.That(cleanup.OperationId, Is.EqualTo(operationId));
            Assert.That(owner.IsBusy, Is.True, "A diagnostic phase must not release the host lease.");
            Assert.That(waiting.Phase, Is.EqualTo(RuntimeDispatchPhase.WaitingForHost), "Snapshots must not change later.");
        }
        finally
        {
            enter.TrySetResult(true);
            release.TrySetResult(true);
            exit.TrySetResult(true);
            await TestDeadline.Complete(pump);
        }
        Assert.That(owner.GetDispatchState().Phase, Is.EqualTo(RuntimeDispatchPhase.Idle));
        Assert.That(owner.GetDispatchState().OperationId, Is.Null);
    }

    [Test]
    public async Task CancelledReservationRemainsVisibleWhileVendorEntryIsOutstanding()
    {
        using var owner = new RuntimeCoordinator();
        var enter = Signal();
        var operationId = Guid.NewGuid();
        var work = owner.Schedule(operationId, Context, _ => throw new AssertionException("Cancelled work ran."));
        var pump = owner.ExecuteNextAsync(async invoke => { await enter.Task; await invoke(); });
        try
        {
            Assert.That(work.TryCancelQueued(), Is.True);
            var state = owner.GetDispatchState();
            Assert.That(state.Phase, Is.EqualTo(RuntimeDispatchPhase.WaitingForHost));
            Assert.That(state.Queued, Is.Zero);
            Assert.That(state.OperationId, Is.EqualTo(operationId));
        }
        finally { enter.TrySetResult(true); await TestDeadline.Complete(pump); }
        Assert.That(owner.GetDispatchState().Phase, Is.EqualTo(RuntimeDispatchPhase.Idle));
        Assert.That(owner.GetDispatchState().OperationId, Is.Null);
    }

    [Test]
    public async Task CancelAfterDispatchBeforeVendorEntryNeverInvokesAndNextWorkCanRun()
    {
        using var owner = new RuntimeCoordinator();
        var entered = Signal();
        var invokeGate = Signal();
        var calls = 0;
        var work = owner.Schedule(Guid.NewGuid(), Context, _ => { calls++; return Task.CompletedTask; });
        var pump = owner.ExecuteNextAsync(async invoke => { entered.SetResult(true); await invokeGate.Task; await invoke(); });
        await entered.Task;
        try
        {
            Assert.That(work.Phase, Is.EqualTo(ReplScheduledPhase.Queued));
            Assert.That(work.TryCancelQueued(), Is.True);
            Assert.That(await work.Completion, Is.EqualTo(ReplScheduleOutcome.CancelledBeforeStart));
        }
        finally { invokeGate.TrySetResult(true); await pump; }
        Assert.That(calls, Is.Zero);
        owner.Schedule(Guid.NewGuid(), Context, _ => { calls++; return Task.CompletedTask; });
        await owner.ExecuteNextAsync(invoke => invoke());
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task UserFailureDoesNotEscapeVendorCallbackOrStopFollowingWork()
    {
        using var owner = new RuntimeCoordinator();
        var work = owner.Schedule(Guid.NewGuid(), Context, _ => throw new InvalidOperationException("user failure"));
        var bridgeReturned = false;
        await owner.ExecuteNextAsync(async invoke => { await invoke(); bridgeReturned = true; });
        Assert.That(bridgeReturned, Is.True);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await work.Completion);
        Assert.That(owner.IsBusy, Is.False);
        var next = owner.Schedule(Guid.NewGuid(), Context, _ => Task.CompletedTask);
        await owner.ExecuteNextAsync(invoke => invoke());
        Assert.That(await next.Completion, Is.EqualTo(ReplScheduleOutcome.CallbackFinished));
    }

    [Test]
    public async Task StopCancelsQueueAndSignalsRunningWorkWithoutReleasingItsLease()
    {
        using var owner = new RuntimeCoordinator();
        var release = Signal();
        var cancelled = Signal();
        var work = owner.Schedule(Guid.NewGuid(), Context, async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult(true));
            await release.Task;
        });
        var pump = owner.ExecuteNextAsync(invoke => invoke());
        try
        {
            var queued = owner.Schedule(Guid.NewGuid(), Context, _ => throw new AssertionException("Queued code executed after stop."));
            owner.Dispose();
            Assert.That(await queued.Completion, Is.EqualTo(ReplScheduleOutcome.CancelledBeforeStart));
            Assert.That(await Task.WhenAny(cancelled.Task, Task.Delay(3000)), Is.SameAs(cancelled.Task));
            Assert.That(work.Completion.IsCompleted, Is.False);
            Assert.That(owner.IsBusy, Is.True);
            Assert.That(work.TryCancelQueued(), Is.False);
            Assert.Throws<ObjectDisposedException>(() => owner.Schedule(Guid.NewGuid(), Context, _ => Task.CompletedTask));
        }
        finally { release.TrySetResult(true); await pump; }
        Assert.That(await work.Completion, Is.EqualTo(ReplScheduleOutcome.CallbackFinished));
    }

    [Test]
    public async Task AllEntryPointsShareOrderAndLeaseIncludesHostCleanup()
    {
        using var owner = new RuntimeCoordinator();
        var order = new List<string>();
        var first = owner.Schedule(Guid.NewGuid(), Context, _ => { order.Add("nrepl"); return Task.CompletedTask; });
        var second = owner.Schedule(Guid.NewGuid(), Context, _ => { order.Add("reload"); return Task.CompletedTask; });
        Assert.That(order, Is.Empty, "Network/UI admission must not execute inline.");
        var cleanup = Signal();
        var pump = owner.ExecuteNextAsync(async invoke => { await invoke(); await cleanup.Task; });
        try
        {
            Assert.That(order, Is.EqualTo(new[] { "nrepl" }));
            Assert.That(owner.IsBusy, Is.True);
            Assert.That(first.Completion.IsCompleted, Is.False);
            await owner.ExecuteNextAsync(invoke => invoke());
            Assert.That(second.Phase, Is.EqualTo(ReplScheduledPhase.Queued));
        }
        finally { cleanup.TrySetResult(true); await pump; }
        Assert.That(await first.Completion, Is.EqualTo(ReplScheduleOutcome.CallbackFinished));
        await owner.ExecuteNextAsync(invoke => invoke());
        Assert.That(order, Is.EqualTo(new[] { "nrepl", "reload" }));
        Assert.That(await second.Completion, Is.EqualTo(ReplScheduleOutcome.CallbackFinished));
    }
}
