using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.Protocol;
using EmbeddedNRepl.Testing;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class BencodeStreamReaderTests
{
    [Test]
    public void ExpiryDuringReadEntryStillClosesAndObservesTheRead()
    {
        var clock = new ManualClock();
        var input = new ControlledInputStream();
        int reads = 0;
        input.OnRead = () => { if (++reads == 2) clock.Advance(TimeSpan.FromSeconds(31)); };
        using var reader = new BencodeStreamReader(input, new ReplLimits(), clock);
        input.Push(Encoding.ASCII.GetBytes("d"));
        Assert.ThrowsAsync<BencodeException>(async () => await TestDeadline.Complete(reader.ReadAsync()));
        Assert.That(input.Closed, Is.True);
        Assert.That(clock.PendingDelays, Is.Zero);
    }

    [Test]
    public async Task AnAlreadyCompleteBufferedFrameDoesNotExpireDuringConsumerIdle()
    {
        var clock = new ManualClock();
        var input = new ControlledInputStream();
        using var reader = new BencodeStreamReader(input, new ReplLimits(), clock);
        input.Push(Encoding.ASCII.GetBytes("dede"));
        Assert.That((await reader.ReadAsync())!.Message, Is.Empty);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.That((await reader.ReadAsync())!.Message, Is.Empty);
    }

    [Test]
    public async Task ABufferedPartialFrameKeepsItsOriginalArrivalTime()
    {
        var clock = new ManualClock();
        var input = new ControlledInputStream();
        using var reader = new BencodeStreamReader(input, new ReplLimits(), clock);
        input.Push(Encoding.ASCII.GetBytes("ded"));
        Assert.That((await reader.ReadAsync())!.Message, Is.Empty);
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = reader.ReadAsync();
        try
        {
            Assert.That(second.IsCompleted, Is.True, "The buffered first byte is already 30 seconds old.");
            Assert.ThrowsAsync<BencodeException>(async () => await second);
        }
        finally
        {
            reader.Dispose();
            try
            {
                await TestDeadline.Complete(second);
            }
            catch (Exception) { /* Observe the pending read even when the assertion detects the regression. */ }
        }
    }

    [Test]
    public async Task IndividualBytesDoNotExtendTheAbsolutePartialFrameDeadline()
    {
        var clock = new ManualClock();
        var input = new ControlledInputStream();
        using var reader = new BencodeStreamReader(input, new ReplLimits(), clock);
        Task<BencodeFrame?> read = reader.ReadAsync();
        Assert.That(read.IsCompleted, Is.False);
        await input.WaitForRead();
        input.Push(Encoding.ASCII.GetBytes("d"));
        await clock.WaitForDelay();
        clock.Advance(TimeSpan.FromSeconds(29));
        input.Push(Encoding.ASCII.GetBytes("1"));
        await clock.WaitForDelay();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.ThrowsAsync<BencodeException>(async () => await TestDeadline.Complete(read));
        Assert.That(input.Closed, Is.True);
        Assert.That(clock.PendingDelays, Is.Zero);
    }

    [Test]
    public async Task IdleBetweenCompleteMessagesHasNoPartialFrameDeadline()
    {
        var clock = new ManualClock();
        var input = new ControlledInputStream();
        using var reader = new BencodeStreamReader(input, new ReplLimits(), clock);
        for (int i = 0; i < 2; i++)
        {
            var read = reader.ReadAsync();
            Assert.That(read.IsCompleted, Is.False);
            await input.WaitForRead();
            clock.Advance(TimeSpan.FromHours(1));
            Assert.That(clock.PendingDelays, Is.Zero);
            Assert.That(read.IsCompleted, Is.False);
            input.Push(Encoding.ASCII.GetBytes("de"));
            Assert.That((await TestDeadline.Complete(read))!.Message, Is.Empty);
        }
    }

    [Test]
    public async Task CancellationClosesAndObservesAnOutstandingRead()
    {
        var input = new ControlledInputStream();
        using var reader = new BencodeStreamReader(input, new ReplLimits(), new ManualClock());
        using var cancel = new CancellationTokenSource();
        var read = reader.ReadAsync(cancel.Token);
        Assert.That(read.IsCompleted, Is.False);
        await input.WaitForRead();
        cancel.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await TestDeadline.Complete(read));
        Assert.That(input.Closed, Is.True);
    }
}
