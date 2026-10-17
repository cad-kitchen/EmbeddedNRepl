using System;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbeddedNRepl.Hosting;
using clojure.lang;
using clojure.lang.Runtime;
using NUnit.Framework;

namespace EmbeddedNRepl.ClojureClr.Tests;

[NonParallelizable]
public sealed class RuntimeAttachmentTests
{
    [OneTimeSetUp]
    public void InitializeOwnerRuntime()
        => Assert.That(RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)")), Is.EqualTo(3L));

    [Test]
    public void CoreAssemblyContainsClojureSourceWithoutClrAdapterTypes()
    {
        var adapter = ClojureModule.ResourceAssembly;
        Assert.That(ClojureModule.ResourceNames, Is.SubsetOf(adapter.GetManifestResourceNames()));
        Assert.That(adapter.GetTypes().Where(type => type.Namespace == "EmbeddedNRepl.ClojureClr"), Is.Empty,
            "The language adapter must not contain handwritten CLR implementation types.");
    }

    [Test]
    public void LoadedClojureNamespaceCreatesTheClrContracts()
    {
        var name = "embedded-nrepl.attachment." + Guid.NewGuid().ToString("N");
        var target = Namespace.findOrCreate(Symbol.intern(name));
        ReferCore(target);
        try
        {
            var adapter = ClojureModule.Create("owner-runtime", name);
            Assert.That(adapter.Evaluator, Is.InstanceOf<IReplEvaluator>());
            Assert.That(adapter.Sessions, Is.InstanceOf<IReplSessionStateFactory>());
            Assert.That(adapter.Sessions.Create(), Is.InstanceOf<IReplSessionState>());
        }
        finally { Namespace.remove(target.Name); }
    }

    [Test]
    public void LoadingPreservesOwnerThreadBindingsAndProcessState()
    {
        var frame = Var.getThreadBindingFrame();
        var bindings = Var.getThreadBindings();
        var directory = Environment.CurrentDirectory;
        var loadPath = Environment.GetEnvironmentVariable("CLOJURE_LOAD_PATH");
        ClojureModule.Load();
        Assert.That(Var.getThreadBindingFrame(), Is.SameAs(frame));
        Assert.That(Var.getThreadBindings(), Is.EqualTo(bindings));
        Assert.That(Environment.CurrentDirectory, Is.EqualTo(directory));
        Assert.That(Environment.GetEnvironmentVariable("CLOJURE_LOAD_PATH"), Is.EqualTo(loadPath));
        Assert.That(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetName().Name == "Clojure"), Is.EqualTo(1));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" \t")]
    public void RejectsMissingRuntimeIdentity(string? runtimeGroupId)
        => Assert.Throws<ArgumentException>(() => ClojureModule.Create(runtimeGroupId!, "clojure.core"));

    [Test]
    public void MissingInitialNamespaceIsRejectedWithoutCreatingIt()
    {
        var missing = "embedded-nrepl.missing." + Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidOperationException>(() => ClojureModule.Create("owner-runtime", missing));
        Assert.That(Namespace.find(Symbol.intern(missing)), Is.Null);
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
}
