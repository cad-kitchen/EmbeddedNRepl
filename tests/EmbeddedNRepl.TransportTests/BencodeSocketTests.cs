using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EmbeddedNRepl.Protocol;
using EmbeddedNRepl.Testing;
using NUnit.Framework;

namespace EmbeddedNRepl.TransportTests;

public sealed class BencodeSocketTests
{
    [Test]
    public async Task FragmentedAndCoalescedFramesAreReadExactlyOnce()
    {
        using var pair = await LoopbackPair.Create();
        using var reader = new BencodeStreamReader(pair.Server.GetStream(), new ReplLimits());
        var first = reader.ReadAsync();
        byte[] fragmented = Encoding.UTF8.GetBytes("d2:op4:eval4:code9:Ж中🙂e");
        foreach (byte next in fragmented)
            await pair.Client.GetStream().WriteAsync(new[] { next }, 0, 1);
        Assert.That((await TestDeadline.Complete(first))!.Message["code"], Is.EqualTo("Ж中🙂"));
        byte[] combined = Encoding.ASCII.GetBytes("d2:op8:describee" + "d2:op5:clonee");
        await pair.Client.GetStream().WriteAsync(combined, 0, combined.Length);
        Assert.That((await TestDeadline.Complete(reader.ReadAsync()))!.Message["op"], Is.EqualTo("describe"));
        Assert.That((await TestDeadline.Complete(reader.ReadAsync()))!.Message["op"], Is.EqualTo("clone"));
        pair.Client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
        Assert.That(await TestDeadline.Complete(reader.ReadAsync()), Is.Null);
    }

    [Test]
    public async Task OversizedHeaderClosesItsStreamAndLeavesAnotherConnectionUsable()
    {
        using var bad = await LoopbackPair.Create();
        using var good = await LoopbackPair.Create();
        var badStream = bad.Server.GetStream();
        using var badReader = new BencodeStreamReader(badStream, new ReplLimits());
        using var goodReader = new BencodeStreamReader(good.Server.GetStream(), new ReplLimits());
        var rejected = badReader.ReadAsync();
        byte[] header = Encoding.ASCII.GetBytes("d1:x1048577:");
        await bad.Client.GetStream().WriteAsync(header, 0, header.Length);
        Assert.ThrowsAsync<BencodeException>(async () => await TestDeadline.Complete(rejected));
        Assert.That(badStream.CanRead, Is.False);
        await good.Client.GetStream().WriteAsync(new byte[] { (byte)'d', (byte)'e' }, 0, 2);
        Assert.That((await TestDeadline.Complete(goodReader.ReadAsync()))!.Message, Is.Empty);
    }

    [Test]
    public async Task DisconnectDuringAMessageCannotProduceAFrame()
    {
        using var pair = await LoopbackPair.Create();
        using var reader = new BencodeStreamReader(pair.Server.GetStream(), new ReplLimits());
        byte[] partial = Encoding.ASCII.GetBytes("d2:op4:eval4:code100:(+ 1");
        await pair.Client.GetStream().WriteAsync(partial, 0, partial.Length);
        pair.Client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
        Assert.ThrowsAsync<BencodeException>(async () => await TestDeadline.Complete(reader.ReadAsync()));
    }

    [Test]
    public async Task DeadlineAbortsARealNetworkRead()
    {
        using var pair = await LoopbackPair.Create();
        var serverStream = pair.Server.GetStream();
        using var reader = new BencodeStreamReader(serverStream,
            new ReplLimits { PartialFrameTimeout = TimeSpan.FromMilliseconds(100) });
        var watch = Stopwatch.StartNew();
        var read = reader.ReadAsync();
        await pair.Client.GetStream().WriteAsync(new byte[] { (byte)'d' }, 0, 1);
        Assert.ThrowsAsync<BencodeException>(async () => await TestDeadline.Complete(read));
        TestContext.Progress.WriteLine("PARTIAL_FRAME_TIMEOUT_OBSERVED_MS=" + watch.Elapsed.TotalMilliseconds);
        Assert.That(serverStream.CanRead, Is.False);
    }
}
