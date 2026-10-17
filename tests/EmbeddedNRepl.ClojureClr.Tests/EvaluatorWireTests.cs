using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Testing;
using clojure.lang;
using clojure.lang.Runtime;
using NUnit.Framework;
using static EmbeddedNRepl.Testing.WirePeer;

namespace EmbeddedNRepl.ClojureClr.Tests;

[NonParallelizable]
public sealed class EvaluatorWireTests
{
    [Test]
    public async Task CalvaDefaultPrinterReturnsValuesWithBoundedFormattingAndVisibleRejections()
    {
        RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)"));
        var initial = Namespace.findOrCreate(Symbol.intern("embedded-nrepl.calva." + Guid.NewGuid().ToString("N")));
        var frame = Var.getThreadBindingFrame();
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, initial));
        try
        {
            RT.var("clojure.core", "refer").invoke(Symbol.intern("clojure.core"));
        }
        finally { Var.resetThreadBindingFrame(frame); }
        var adapter = ClojureModule.Create("test-runtime", initial.Name.ToString());
        var fixture = new ServerFixture();
        var server = new ReplServer(new ReplServices(fixture.Services.Identity,
            adapter.Evaluator, adapter.Sessions, fixture.Scheduler, fixture.Host));
        try
        {
            using var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!);
            await peer.Send(Message("op", "clone", "id", "clone"));
            var session = (string)(await peer.Receive())["new-session"];
            async Task<List<Dictionary<string, object>>> Eval(string code, Dictionary<string, object>? options = null,
                int quota = 65536, bool pretty = true)
            {
                var request = Message("op", "eval", "id", "calva", "session", session, "ns", initial.Name.ToString(),
                    "code", code, "file", "core.cljr", "line", 511L, "column", 1L, "pprint", 1L,
                    "nrepl.middleware.print/quota", (long)quota);
                if (pretty)
                {
                    request["nrepl.middleware.print/print"] = "cider.nrepl.pprint/pprint";
                    request["nrepl.middleware.print/options"] = options ?? Message("right-margin", 120L, "length", 50L);
                }
                await peer.Send(request);
                for (var attempt = 0; attempt < 100 && fixture.Scheduler.Queued == 0; attempt++)
                    await Task.Delay(5);
                Assert.That(fixture.Scheduler.Queued, Is.EqualTo(1), "Calva eval must be admitted, not silently rejected.");
                await fixture.Scheduler.RunNextAsync();
                var responses = new List<Dictionary<string, object>>();
                do
                {
                    responses.Add(await peer.Receive());
                }
                while (!responses.Last().TryGetValue("status", out var status) || !((IEnumerable<object>)status).Contains("done"));
                Assert.That(responses.All(r => Equals(r["id"], "calva") && Equals(r["session"], session)), Is.True);
                return responses;
            }
            string[] Values(List<Dictionary<string, object>> responses) => responses.Where(r => r.ContainsKey("value"))
                .Select(r => (string)r["value"]).ToArray();
            Assert.That(Values(await Eval("(+ 2 3) nil \"я😀\"")), Is.EqualTo(new[] { "5", "nil", "\"я😀\"" }));
            var narrow = Values(await Eval("[1111 2222 3333 4444]", Message("right-margin", 8L))).Single();
            Assert.That(narrow, Does.Contain("\n"));
            Assert.That(RT.readString(narrow).ToString(), Is.EqualTo("[1111 2222 3333 4444]"));
            Assert.That(Values(await Eval("(range)", Message("length", 3L))).Single(), Does.Contain("0 1 2 ..."));
            Assert.That(Values(await Eval("[[[1]]]", Message("level", 1L))).Single(), Is.EqualTo("[#]"));
            var capped = await Eval("(range)", Message("length", 10000L));
            Assert.That(Values(capped).Single(), Does.Contain("99").And.Not.Contain("100 "));
            var quota = await Eval("(apply str (repeat 100 \"я😀\"))", quota: 12);
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(Values(quota).Single()), Is.LessThanOrEqualTo(12));
            Assert.That(quota.Last()["status"], Does.Contain("embedded-nrepl/output-truncated"));
            var stdout = await Eval("(println \"hello\") (+ 2 3)");
            Assert.That(string.Concat(stdout.Where(r => r.ContainsKey("out")).Select(r => r["out"])), Does.Contain("hello"));
            Assert.That(Values(stdout).Last(), Is.EqualTo("5"));
            Assert.That(Values(await Eval("[0 1 2 3]", pretty: false)).Single(), Is.EqualTo("[0 1 2 3]"));
            await peer.Send(Message("op", "eval", "id", "unsupported", "session", session, "code", "(throw (System.Exception.))",
                "nrepl.middleware.print/print", "custom/printer"));
            var rejected = await peer.Receive();
            Assert.That(rejected["err"], Does.Contain("custom/printer"));
            Assert.That(rejected.ContainsKey("ex"), Is.True, "Calva needs an exception field to reject instead of showing an empty value.");
            Assert.That(rejected["status"], Is.EqualTo(new[] { "error", "embedded-nrepl/unsupported-option", "done" }));
            Assert.That(fixture.Scheduler.Queued, Is.Zero);
        }
        finally { await server.StopAsync(); Namespace.remove(initial.Name); }
    }

    [Test]
    public async Task RealEvaluationDeliversCorrelatedValuesDiagnosticsTruncationAndSessionRecovery()
    {
        RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)"));
        var ns = Namespace.findOrCreate(Symbol.intern("embedded-nrepl.wire." + Guid.NewGuid().ToString("N")));
        var frame = Var.getThreadBindingFrame();
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, ns));
        try
        {
            RT.var("clojure.core", "refer").invoke(Symbol.intern("clojure.core"));
        }
        finally { Var.resetThreadBindingFrame(frame); }
        var adapter = ClojureModule.Create("test-runtime", ns.Name.ToString());
        var fixture = new ServerFixture();
        var server = new ReplServer(new ReplServices(fixture.Services.Identity,
            adapter.Evaluator, adapter.Sessions, fixture.Scheduler, fixture.Host));
        try
        {
            using var peer = await Connect(server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!);
            await peer.Send(Message("op", "clone", "id", "clone"));
            var session = (string)(await peer.Receive())["new-session"];
            async Task<List<Dictionary<string, object>>> Eval(string code, string? selectedNs = null)
            {
                var request = Message("op", "eval", "id", "evaluate", "session", session, "code", code);
                if (selectedNs != null)
                    request.Add("ns", selectedNs);
                await peer.Send(request);
                for (var attempt = 0; attempt < 100 && fixture.Scheduler.Queued == 0; attempt++)
                    await Task.Delay(5);
                Assert.That(fixture.Scheduler.Queued, Is.EqualTo(1));
                await fixture.Scheduler.RunNextAsync();
                var responses = new List<Dictionary<string, object>>();
                do
                {
                    responses.Add(await peer.Receive());
                }
                while (!responses.Last().TryGetValue("status", out var status) || !((IEnumerable<object>)status).Contains("done"));
                Assert.That(responses.All(r => Equals(r["id"], "evaluate") && Equals(r["session"], session)), Is.True);
                return responses;
            }
            var success = await Eval("(def saved-writer *out*) (+ 20 22)");
            Assert.That(success.Where(r => r.ContainsKey("value")).Last()["ns"], Is.EqualTo(ns.Name.ToString()));
            var failed = await Eval("(println \"marker\") 42 (throw (System.Exception. \"wire failure\")) (def forbidden 1)");
            Assert.That(string.Concat(failed.Where(r => r.ContainsKey("out")).Select(r => r["out"])), Does.Contain("marker"));
            var diagnostic = failed.Single(r => r.ContainsKey("ex"));
            Assert.That(diagnostic["root-ex"], Is.EqualTo("System.Exception"));
            Assert.That(diagnostic["embedded-nrepl/phase"], Is.EqualTo("eval"));
            Assert.That(diagnostic["err"], Does.Contain("wire failure"));
            Assert.That(failed.Last()["status"], Is.EqualTo(new[] { "error", "eval-error", "done" }));
            Assert.That(ns.FindInternedVar(Symbol.intern("forbidden")), Is.Null);
            var bounded = await Eval("(range) 7");
            Assert.That(bounded.First(r => r.ContainsKey("value"))["nrepl.middleware.print/truncated"], Is.EqualTo(new[] { "value" }));
            Assert.That(bounded.Where(r => r.ContainsKey("value")).Last()["value"], Is.EqualTo("7"));
            Assert.That(bounded.Last()["status"], Is.EqualTo(new[] { "embedded-nrepl/output-truncated", "done" }));
            var missing = await Eval("(def never 1)", "embedded-nrepl.does-not-exist");
            Assert.That(missing.Last()["status"], Is.EqualTo(new[] { "error", "namespace-not-found", "done" }));
            var old = (TextWriter)ns.FindInternedVar(Symbol.intern("saved-writer")).deref();
            old.Write("late");
            var recovered = await Eval("*1");
            Assert.That(recovered.Single(r => r.ContainsKey("value"))["value"], Is.EqualTo("7"));
            Assert.That(recovered.Any(r => r.ContainsKey("out")), Is.False);
            await peer.Send(Message("op", "stdin", "stdin", "hello", "id", "input"));
            Assert.That((await peer.Receive())["status"], Is.EqualTo(new[] { "unknown-op", "done" }));
        }
        finally { await server.StopAsync(); Namespace.remove(ns.Name); }
    }
}
