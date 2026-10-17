using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Testing;
using NUnit.Framework;
using static EmbeddedNRepl.Testing.WirePeer;

namespace EmbeddedNRepl.TransportTests;

public sealed class SessionFailureTests
{
    [Test]
    public async Task SessionLimitUnknownParentAndUnknownClosePreserveExistingSessions()
    {
        var fixture = new ServerFixture();
        var server = new ReplServer(fixture.Services, new ReplLimits { PersistentSessions = 1 });
        try
        {
            using var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!);
            await peer.Send(Message("op", "clone", "session", "unknown", "id", "parent"));
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "error", "unknown-session", "done" }));
            await peer.Send(Message("op", "clone", "id", "first"));
            var session = (string)(await peer.Receive())["new-session"];
            await peer.Send(Message("op", "clone", "id", "overflow"));
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "error", "session-limit", "done" }));
            await peer.Send(Message("op", "close", "session", "unknown"));
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "error", "unknown-session", "done" }));
            await peer.Send(Message("op", "ls-sessions"));
            Assert.That((await peer.Receive())["sessions"], Is.EqualTo(new[] { session }));
            await peer.Send(Message("op", "close", "session", session));
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "session-closed", "done" }));
            Assert.That(fixture.States.Active, Is.Zero);
            await peer.Send(Message("op", "eval", "session", session, "code", "must-not-run"));
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "error", "unknown-session", "done" }));
            Assert.That(fixture.Evaluator.Calls, Is.Empty);
        }
        finally { await server.StopAsync(); }
    }

    [Test]
    public async Task FactoryFailureIsReportedAndTheConnectionCanCreateASessionAfterRepair()
    {
        var fixture = new ServerFixture();
        fixture.States.Before = operation => { if (operation == "create") throw new InvalidOperationException("factory failed"); };
        var server = new ReplServer(fixture.Services);
        try
        {
            using var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!);
            await peer.Send(Message("op", "clone", "id", "failed"));
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "error", "session-error", "done" }));
            Assert.That(fixture.States.Active, Is.Zero);
            fixture.States.Before = _ => { };
            await peer.Send(Message("op", "clone", "id", "retry"));
            Assert.That((await peer.Receive()).ContainsKey("new-session"), Is.True);
        }
        finally { await server.StopAsync(); }
        Assert.That(fixture.States.Active, Is.Zero);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task EvaluationFailureAndThrownCallbacksReleaseEphemeralState(bool throwing)
    {
        var fixture = new ServerFixture();
        fixture.Evaluator.Behavior = (_, _, _, _, _) => throwing
            ? throw new InvalidOperationException("evaluation failed")
            : Task.FromResult(ReplEvaluationOutcome.Failed);
        var server = new ReplServer(fixture.Services);
        try
        {
            using var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!);
            await peer.Send(Message("op", "eval", "id", "failure", "code", "fail"));
            await WaitQueued(fixture);
            await fixture.Scheduler.RunNextAsync();
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "error", "eval-error", "done" }));
            Assert.That(fixture.States.Active, Is.Zero);
            await peer.Send(Message("op", "describe"));
            Assert.That((await peer.Receive()).ContainsKey("ops"), Is.True);
        }
        finally { await server.StopAsync(); }
    }

    [Test]
    public async Task InvalidUntypedCorrelationFieldsAreNotEchoedAsTrustedIds()
    {
        var fixture = new ServerFixture();
        var server = new ReplServer(fixture.Services);
        try
        {
            using var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!);
            await peer.Send(Message("op", 1L, "id", 2L, "session", 3L));
            var response = await peer.Receive();
            Assert.That(response.ContainsKey("id"), Is.False);
            Assert.That(response.ContainsKey("session"), Is.False);
            Assert.That(response["status"], Is.EqualTo(new[] { "error", "embedded-nrepl/invalid-request", "done" }));
        }
        finally { await server.StopAsync(); }
    }

    private static async Task WaitQueued(ServerFixture fixture)
    {
        for (var attempt = 0; attempt < 100 && fixture.Scheduler.Queued == 0; attempt++)
            await Task.Delay(5);
        Assert.That(fixture.Scheduler.Queued, Is.EqualTo(1), "Request did not reach the owner queue.");
    }
}
