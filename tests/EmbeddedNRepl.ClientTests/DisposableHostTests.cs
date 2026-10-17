using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EmbeddedNRepl.Testing;
using NUnit.Framework;
using static EmbeddedNRepl.Testing.WirePeer;

namespace EmbeddedNRepl.ClientTests;

public sealed class DisposableHostTests
{
    internal static async Task<List<Dictionary<string, object>>> Done(WirePeer peer)
    {
        var frames = new List<Dictionary<string, object>>();
        do
        {
            frames.Add(await peer.Receive());
        }
        while (!frames.Last().TryGetValue("status", out var status) || !((IEnumerable<object>)status).Contains("done"));
        return frames;
    }

    [Test]
    public async Task InfiniteSequencesAreBoundedAndStdinReturnsEof()
    {
        using var host = await ConsoleProcess.Start();
        using var peer = await Connect(IPAddress.Loopback, host.Port);
        await peer.Send(Message("op", "eval", "code", "(range) (read-line)", "id", "infinite"));
        var frames = await Done(peer);
        var values = frames.Where(frame => frame.ContainsKey("value")).ToArray();
        Assert.That(values, Has.Length.EqualTo(2));
        Assert.That(values[0]["value"], Does.EndWith("99 ...)"));
        Assert.That(values[1]["value"], Is.EqualTo("nil"));
        Assert.That(frames.Last()["status"], Is.EqualTo(new[] { "embedded-nrepl/output-truncated", "done" }));
        await host.Stop();
    }
}
