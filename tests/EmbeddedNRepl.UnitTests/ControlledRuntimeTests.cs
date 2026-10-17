using System;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Testing;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class ControlledRuntimeTests
{
    [Test]
    public async Task HarnessSeparatesQueuedCancellationSignalsAndActualCompletion()
    {
        var scheduler = new ControlledScheduler();
        var host = new FakeHost();
        using var barrier = new EvaluationBarrier();
        var first = scheduler.Schedule(Guid.NewGuid(), host.Capture(), async token => await barrier.Evaluate(token));
        bool invoked = false;
        var queued = scheduler.Schedule(Guid.NewGuid(), host.Capture(), token => { invoked = true; return Task.CompletedTask; });
        Assert.That(first.Phase, Is.EqualTo(ReplScheduledPhase.Queued));
        Assert.That(scheduler.Active, Is.Zero);
        var running = scheduler.RunNextAsync();
        try
        {
            await barrier.Entered;
            first.RequestCancellation();
            await barrier.Cancelled;
            Assert.That(first.Completion.IsCompleted, Is.False);
            Assert.That(scheduler.Active, Is.EqualTo(1));
            Assert.That(queued.TryCancelQueued(), Is.True);
            Assert.That(queued.TryCancelQueued(), Is.False);
            Assert.That(await queued.Completion, Is.EqualTo(ReplScheduleOutcome.CancelledBeforeStart));
        }
        finally { barrier.Release(); await TestDeadline.Complete(running); }
        Assert.That(await first.Completion, Is.EqualTo(ReplScheduleOutcome.CallbackFinished));
        Assert.That(invoked, Is.False);
        Assert.That(scheduler.Queued, Is.Zero);
        Assert.That(scheduler.Active, Is.Zero);
        Assert.That(scheduler.Events.Snapshot().Length, Is.EqualTo(5));
    }

    [Test]
    public void PublishedSnapshotAndResourceAccountingAreIndependentOfLiveState()
    {
        var factory = new FakeStateFactory();
        var parent = (FakeState)factory.Create();
        parent.Value = 1;
        var snapshot = factory.Snapshot(parent);
        parent.Value = 2;
        var child = (FakeState)factory.Clone(snapshot);
        Assert.That(child.Value, Is.EqualTo(1));
        Assert.That(factory.Active, Is.EqualTo(2));
        factory.Release(parent);
        factory.Release(child);
        Assert.That(factory.Active, Is.Zero);
    }
}
