using System;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;
using clojure.lang;
using NUnit.Framework;

namespace EmbeddedNRepl.ClojureClr.Tests;

[NonParallelizable]
public sealed class EmbeddingTests
{
    [OneTimeSetUp]
    public void InitializeRuntime()
        => RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)"));

    [Test]
    public async Task AttachPreservesBindingsAndPassesQuotedIdentitiesAsData()
    {
        const string group = "runtime\"\\\n\tЖ";
        var ownerNamespace = Namespace.findOrCreate(Symbol.intern("isolated." + Guid.NewGuid().ToString("N")));
        var frame = Var.getThreadBindingFrame();
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, ownerNamespace));
        var ownerFrame = Var.getThreadBindingFrame();
        try
        {
            var adapter = ClojureAdapter.Attach(group,
                source => RT.var("clojure.core", "load-string").invoke(source), "clojure.core");
            Assert.That(Var.getThreadBindingFrame(), Is.SameAs(ownerFrame));
            Assert.That(RT.CurrentNSVar.deref(), Is.SameAs(ownerNamespace));
            var second = ClojureAdapter.Attach(group,
                source => RT.var("clojure.core", "load-string").invoke(source), "clojure.core");
            Assert.That(second.Sessions, Is.Not.SameAs(adapter.Sessions));
            var state = adapter.Sessions.Create();
            try
            {
                var output = new EvaluatorTests.RecordingOutput();
                var outcome = await adapter.Evaluator.EvaluateAsync(new ReplEvaluation("(+ 1 2)"), state,
                    new ReplExecutionContext(group), output, CancellationToken.None);
                Assert.That(outcome, Is.EqualTo(ReplEvaluationOutcome.Succeeded));
                Assert.That(output.Values, Is.EqualTo(new[] { "3" }));
            }
            finally { adapter.Sessions.Release(state); }
        }
        finally
        {
            Var.resetThreadBindingFrame(frame);
            Namespace.remove(ownerNamespace.Name);
        }
    }

    [Test]
    public void FailedAttachmentDoesNotCreateANamespaceOrLeakOwnerBindings()
    {
        var frame = Var.getThreadBindingFrame();
        var missing = "missing." + Guid.NewGuid().ToString("N");
        Assert.Catch(() => ClojureAdapter.Attach("owner",
            source => RT.var("clojure.core", "load-string").invoke(source), missing));
        Assert.That(Namespace.find(Symbol.intern(missing)), Is.Null);
        Assert.That(Var.getThreadBindingFrame(), Is.SameAs(frame));
    }
}
