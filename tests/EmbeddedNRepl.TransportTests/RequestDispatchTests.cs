using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using EmbeddedNRepl.Testing;
using NUnit.Framework;
using static EmbeddedNRepl.Testing.WirePeer;

namespace EmbeddedNRepl.TransportTests;

public sealed class RequestDispatchTests
{
    [Test]
    public async Task DescribeAdvertisesOnlyTheMinimalProtocolSurface()
    {
        var fixture = new ServerFixture();
        var server = new ReplServer(fixture.Services);
        try
        {
            using var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!);
            await peer.Send(Message("op", "describe", "id", "minimal"));
            var response = await peer.Receive();
            var operations = (Dictionary<string, object>)response["ops"];
            Assert.That(operations.Keys, Is.EquivalentTo(new[] { "describe", "clone", "close", "ls-sessions", "eval" }));
            Assert.That(response["status"], Is.EqualTo(new[] { "done" }));
        }
        finally { await server.StopAsync(); }
    }

    [Test]
    public async Task InvalidAndUnsupportedRequestsNeverReachTheExecutor()
    {
        var fixture = new ServerFixture();
        var server = new ReplServer(fixture.Services);
        try
        {
            using (var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!))
            {
                await peer.Send(Message("op", "eval", "id", "invalid", "code", 1L),
                    Message("op", "eval", "id", "unsupported", "code", "(side-effect)", "eval", "custom/function"));
                var invalid = await peer.Receive();
                Assert.That(invalid["id"], Is.EqualTo("invalid"));
                Assert.That(invalid["status"], Is.EqualTo(new[] { "error", "embedded-nrepl/invalid-request", "done" }));
                var unsupported = await peer.Receive();
                Assert.That(unsupported["id"], Is.EqualTo("unsupported"));
                Assert.That(unsupported["status"], Is.EqualTo(new[] { "error", "embedded-nrepl/unsupported-option", "done" }));
                Assert.That(fixture.Scheduler.Queued, Is.Zero);
                Assert.That(fixture.Evaluator.Calls, Is.Empty);
                Assert.That(fixture.States.Active, Is.Zero);
            }
        }
        finally { await server.StopAsync(); }
    }
    [Test]
    public async Task UnknownOperationsAreCorrelatedAndDoNotExecute()
    {
        var fixture = new ServerFixture();
        var server = new ReplServer(fixture.Services);
        try
        {
            using (var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!))
            {
                await peer.Send(Message("op", "stdin", "id", "я😀", "session", "original"), Message("op", "lookup"));
                var first = await peer.Receive();
                Assert.That(first["id"], Is.EqualTo("я😀"));
                Assert.That(first["session"], Is.EqualTo("original"));
                Assert.That(first["status"], Is.EqualTo(new[] { "unknown-op", "done" }));
                var second = await peer.Receive();
                Assert.That(second.ContainsKey("id"), Is.False);
                Assert.That(second["status"], Is.EqualTo(new[] { "unknown-op", "done" }));
                Assert.That(fixture.Scheduler.Queued, Is.Zero);
                Assert.That(fixture.Evaluator.Calls, Is.Empty);
                Assert.That(fixture.States.Active, Is.Zero);
            }
        }
        finally { await server.StopAsync(); }
    }
}
