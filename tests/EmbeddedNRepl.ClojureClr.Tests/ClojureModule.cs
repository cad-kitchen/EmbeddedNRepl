using System;
using System.IO;
using System.Reflection;
using EmbeddedNRepl;
using EmbeddedNRepl.Hosting;
using clojure.lang;

namespace EmbeddedNRepl.ClojureClr.Tests;

internal static class ClojureModule
{
    internal static readonly string[] ResourceNames =
    {
        "embedded-nrepl.session.cljr",
        "embedded-nrepl.printing.cljr",
        "embedded-nrepl.evaluation.cljr",
        "embedded-nrepl.runtime.cljr"
    };
    private static readonly object Gate = new object();

    internal static Assembly ResourceAssembly => typeof(ReplServer).Assembly;

    internal static (IReplEvaluator Evaluator, IReplSessionStateFactory Sessions) Create(
        string runtimeGroupId, string initialNamespace)
    {
        Load();
        var adapter = (IPersistentVector)RT.var("embedded-nrepl.runtime", "create-adapter")
            .invoke(runtimeGroupId, initialNamespace);
        return ((IReplEvaluator)adapter.nth(0), (IReplSessionStateFactory)adapter.nth(1));
    }

    internal static void Load()
    {
        lock (Gate)
        {
            var runtimeNamespace = Namespace.find(Symbol.intern("embedded-nrepl.runtime"));
            if (runtimeNamespace?.FindInternedVar(Symbol.intern("create-adapter")) != null)
                return;
            var frame = Var.getThreadBindingFrame();
            try
            {
                foreach (var resourceName in ResourceNames)
                {
                    using var stream = ResourceAssembly.GetManifestResourceStream(resourceName) ??
                        throw new InvalidOperationException("An embedded ClojureCLR module source is missing: " + resourceName);
                    using var source = new StreamReader(stream);
                    Compiler.load(source, resourceName, resourceName, resourceName);
                }
            }
            finally { Var.resetThreadBindingFrame(frame); }
        }
    }
}
