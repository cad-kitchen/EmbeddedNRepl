using System;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.ConsoleHost;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Testing;
using NUnit.Framework;

namespace EmbeddedNRepl.ClojureClr.Tests;

public sealed class ConsoleSchedulerTests
{
    [Test]
    public async Task OwnerSerializesWorkAndCancellationNeverRunsUserHandlersOnControlThread()
    {
        var ownerThread = 0;
        using var release = new ManualResetEventSlim();
        using var cancelRelease = new ManualResetEventSlim();
        using var cancelEntered = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new ConsoleRuntimeScheduler(() => ownerThread = Thread.CurrentThread.ManagedThreadId);
        var context = new ReplExecutionContext("standalone", null, null, null, ReplExecutionMode.Application);
        try
        {
            var workThread = 0;
            var first = scheduler.Schedule(Guid.NewGuid(), context, token =>
            {
                workThread = Thread.CurrentThread.ManagedThreadId;
                using var registration = token.Register(() => { cancelEntered.Set(); cancelRelease.Wait(); });
                started.SetResult(true);
                release.Wait();
                return Task.CompletedTask;
            });
            await TestDeadline.Complete(started.Task);
            var invoked = false;
            var queued = scheduler.Schedule(Guid.NewGuid(), context, _ => { invoked = true; return Task.CompletedTask; });
            Assert.That(queued.TryCancelQueued(), Is.True);
            Assert.That(await queued.Completion, Is.EqualTo(ReplScheduleOutcome.CancelledBeforeStart));
            Assert.That(invoked, Is.False);
            var signal = Task.Run(() => first.RequestCancellation());
            await TestDeadline.Complete(signal);
            Assert.That(cancelEntered.Wait(3000), Is.True);
            Assert.That(first.Completion.IsCompleted, Is.False);
            Assert.That(first.Phase, Is.EqualTo(ReplScheduledPhase.Running));
            Assert.That(first.TryCancelQueued(), Is.False);
            Assert.That(workThread, Is.EqualTo(ownerThread));
            cancelRelease.Set();
            release.Set();
            await TestDeadline.Complete(first.Completion);
            Assert.That(first.Phase, Is.EqualTo(ReplScheduledPhase.Finished));
            scheduler.Dispose();
            await TestDeadline.Complete(scheduler.Completion);
            Assert.Throws<ObjectDisposedException>(() => scheduler.Schedule(Guid.NewGuid(), context, _ => Task.CompletedTask));
        }
        finally { cancelRelease.Set(); release.Set(); scheduler.Dispose(); await TestDeadline.Complete(scheduler.Completion); }
    }
}
