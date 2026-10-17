using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Testing;
using NUnit.Framework;
using static EmbeddedNRepl.Testing.WirePeer;

namespace EmbeddedNRepl.TransportTests;

public sealed class MinimalEvaluationTests
{
    [Test]
    public async Task CloneEvalAndClosePreserveOneSession()
    {
        var fixture = new ServerFixture();
        fixture.Evaluator.Behavior = (evaluation, state, context, output, token) =>
        {
            var session = (FakeState)state;
            session.Value++;
            output.TryWrite(ReplOutputKind.Value, session.Value.ToString(), 0, session.Value.ToString().Length);
            return Task.FromResult(ReplEvaluationOutcome.Succeeded);
        };
        var server = new ReplServer(fixture.Services);
        try
        {
            using var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!);
            await peer.Send(Message("op", "clone", "id", "clone"));
            var clone = await peer.Receive();
            var session = (string)clone["new-session"];

            Assert.That(await Eval(peer, fixture, session, "one"), Is.EqualTo("1"));
            Assert.That(await Eval(peer, fixture, session, "two"), Is.EqualTo("2"));

            await peer.Send(Message("op", "close", "id", "close", "session", session));
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "session-closed", "done" }));
            Assert.That(fixture.States.Active, Is.Zero);
        }
        finally { await server.StopAsync(); }
    }

    private static async Task<string> Eval(WirePeer peer, ServerFixture fixture, string session, string id)
    {
        await peer.Send(Message("op", "eval", "id", id, "session", session, "code", "ignored"));
        for (var attempt = 0; attempt < 100 && fixture.Scheduler.Queued == 0; attempt++)
            await Task.Delay(5);
        Assert.That(fixture.Scheduler.Queued, Is.EqualTo(1));
        await fixture.Scheduler.RunNextAsync();
        var responses = new List<Dictionary<string, object>> { await peer.Receive(), await peer.Receive() };
        Assert.That(responses.Last()["status"], Is.EqualTo(new[] { "done" }));
        return (string)responses.Single(response => response.ContainsKey("value"))["value"];
    }
}
