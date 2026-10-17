using System;
using System.Linq;
using System.Threading;
using EmbeddedNRepl.Hosting;
using clojure.lang;
using clojure.lang.Runtime;
using NUnit.Framework;

namespace EmbeddedNRepl.ClojureClr.Tests;

[NonParallelizable]
public sealed class SessionBindingTests
{
    private IReplSessionStateFactory factory = null!;
    private IReplEvaluator evaluator = null!;
    private Namespace initial = null!;
    private Namespace alternate = null!;
    private object originalFrame = null!;
    private readonly ReplExecutionContext context =
        new ReplExecutionContext("binding-runtime", null, null, null, ReplExecutionMode.Application);

    [SetUp]
    public void PrepareOwner()
    {
        RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)"));
        originalFrame = Var.getThreadBindingFrame();
        initial = Namespace.findOrCreate(Symbol.intern("embedded-nrepl.bindings.initial." + Guid.NewGuid().ToString("N")));
        alternate = Namespace.findOrCreate(Symbol.intern("embedded-nrepl.bindings.alternate." + Guid.NewGuid().ToString("N")));
        ReferCore(initial);
        ReferCore(alternate);
        (evaluator, factory) = ClojureModule.Create(context.RuntimeGroupId, initial.Name.ToString());
    }

    [TearDown]
    public void RestoreOwner()
    {
        if (originalFrame != null)
            Var.resetThreadBindingFrame(originalFrame);
        if (initial != null)
            Namespace.remove(initial.Name);
        if (alternate != null)
            Namespace.remove(alternate.Name);
    }

    private static void ReferCore(Namespace target)
    {
        var frame = Var.getThreadBindingFrame();
        Var.pushThreadBindings(RT.map(RT.CurrentNSVar, target));
        try
        {
            RT.var("clojure.core", "refer").invoke(Symbol.intern("clojure.core"));
        }
        finally { Var.resetThreadBindingFrame(frame); }
    }

    private EvaluatorTests.RecordingOutput Run(IReplSessionState state, string code, string? selectedNamespace = null)
    {
        var output = new EvaluatorTests.RecordingOutput();
        var outcome = evaluator.EvaluateAsync(new ReplEvaluation(code, selectedNamespace), state,
            context, output, CancellationToken.None).GetAwaiter().GetResult();
        Assert.That(outcome, Is.EqualTo(ReplEvaluationOutcome.Succeeded));
        return output;
    }

    [Test]
    public void ClojureDefinesTheSupportedInitialBindingSet()
    {
        Assert.That(Run(factory.Create(), "[*1 *2 *3 *e]").Values.Single(), Is.EqualTo("[nil nil nil nil]"));
        Assert.Throws<ArgumentException>(() => ClojureModule.Create(context.RuntimeGroupId, " "));
        var missing = "embedded-nrepl.bindings.absent." + Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidOperationException>(() => ClojureModule.Create(context.RuntimeGroupId, missing));
        Assert.That(Namespace.find(Symbol.intern(missing)), Is.Null);
    }

    [Test]
    public void OptionalAndRequiredRuntimeBindingsAreValidatedWithoutRepair()
    {
        var core = Namespace.find(Symbol.intern("clojure.core"));
        var optional = core.FindInternedVar(Symbol.intern("*assert*"));
        try
        {
            core.unmap(optional.Symbol);
            var limited = ClojureModule.Create(context.RuntimeGroupId, initial.Name.ToString()).Sessions;
            Assert.That(limited.Create(), Is.Not.Null);
        }
        finally { core.refer(optional.Symbol, optional); }

        var required = core.FindInternedVar(Symbol.intern("*1"));
        try
        {
            required.setDynamic(false);
            Assert.Throws<InvalidOperationException>(() =>
                ClojureModule.Create(context.RuntimeGroupId, initial.Name.ToString()));
        }
        finally { required.setDynamic(); }
    }

    [Test]
    public void SessionsAndPublishedSnapshotsKeepIndependentHistory()
    {
        var parent = factory.Create();
        var other = factory.Create();
        Run(parent, "41");
        var published = factory.Snapshot(parent);
        Run(parent, "99");
        Run(other, "22");
        var oldClone = factory.Clone(published);
        Assert.That(Run(parent, "*1").Values.Single(), Is.EqualTo("99"));
        Assert.That(Run(other, "*1").Values.Single(), Is.EqualTo("22"));
        Assert.That(Run(oldClone, "*1").Values.Single(), Is.EqualTo("41"));
    }

    [Test]
    public void NamespaceAndPrintSettingsPersistWhileAnExplicitNamespaceIsTemporary()
    {
        var state = factory.Create();
        Run(state, "(in-ns '" + alternate.Name + ") (set! clojure.core/*print-length* 2) (range 5)");
        var persisted = Run(state, "[(str (ns-name *ns*)) *print-length* *1]");
        Assert.That(persisted.Values.Single(), Is.EqualTo("[\"" + alternate.Name + "\" 2 ...]"));

        var explicitState = factory.Create();
        Run(explicitState, "(in-ns 'clojure.core) 7", alternate.Name.ToString());
        var after = Run(explicitState, "[(str (ns-name *ns*)) *1]");
        Assert.That(after.Values.Single(), Is.EqualTo("[\"" + initial.Name + "\" 7]"));
    }

    [Test]
    public void OwnershipReleaseAndBusyStateAreEnforcedByTheClrBoundary()
    {
        var state = factory.Create();
        initial.intern(Symbol.intern("release-during-eval")).bindRoot(new Callback(() =>
        {
            Assert.Throws<InvalidOperationException>(() => factory.Release(state));
            return 42L;
        }));
        Assert.That(Run(state, "(release-during-eval)").Values.Single(), Is.EqualTo("42"));

        var foreignFactory = ClojureModule.Create(context.RuntimeGroupId, alternate.Name.ToString()).Sessions;
        var foreign = foreignFactory.Create();
        Assert.Throws<ArgumentException>(() => factory.Snapshot(foreign));
        Assert.Throws<ArgumentException>(() => factory.Release(foreign));
        Assert.Throws<ArgumentException>(() => factory.Clone(foreignFactory.Snapshot(foreign)));

        factory.Release(state);
        factory.Release(state);
        Assert.Throws<ObjectDisposedException>(() => Run(state, "1"));
    }

    private sealed class Callback : AFn
    {
        private readonly Func<object> action;
        internal Callback(Func<object> action) => this.action = action;
        public override object invoke() => action();
    }
}
