using System;
using System.Net;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EmbeddedNRepl.Hosting;
using clojure.lang;
using clojure.lang.Runtime;

namespace EmbeddedNRepl.ConsoleHost;

internal static class Program
{

    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--port" || !int.TryParse(args[1], out var port) || port < 0 || port > 65535)
        {
            Console.Error.WriteLine("Usage: EmbeddedNRepl.ConsoleHost --port <0..65535>. Developer access on 127.0.0.1; type quit to stop.");
            return 2;
        }
        try
        {
            var group = Guid.NewGuid().ToString();
            IReplSessionStateFactory? sessions = null;
            IReplEvaluator? evaluator = null;
            var languageVersion = "";
            var adapterAssembly = typeof(ReplServer).Assembly;
            using var scheduler = new DedicatedThreadScheduler(() =>
            {
                // This executable owns initialization. The reusable adapter only attaches afterward.
                RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)"));
                var frame = Var.getThreadBindingFrame();
                var user = Namespace.findOrCreate(Symbol.intern("user"));
                Var.pushThreadBindings(RT.map(RT.CurrentNSVar, user));
                try
                {
                    RT.var("clojure.core", "refer").invoke(Symbol.intern("clojure.core"));
                }
                finally { Var.resetThreadBindingFrame(frame); }
                var adapter = ClojureAdapter.Attach(group, source => RT.var("clojure.core", "load-string").invoke(source));
                (evaluator, sessions) = (adapter.Evaluator, adapter.Sessions);
                languageVersion = (string)RT.var("clojure.core", "clojure-version").invoke();
            });
            var version = adapterAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            var target = typeof(Program).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
            var identity = new ReplHostIdentity("embedded-nrepl-console", version, Guid.NewGuid().ToString(), group,
                "standalone-console", version, version, target, version, languageVersion);
            var server = new ReplServer(new ReplServices(identity, evaluator!, sessions!, scheduler, new ApplicationReplContext(group)));
            var endpoint = server.Start(true, new IPEndPoint(IPAddress.Loopback, port))!;
            Console.WriteLine("ACTUAL_RUNTIME=" + RuntimeInformation.FrameworkDescription);
            Console.WriteLine("HOST_SESSION_ID=" + identity.HostSessionId);
            Console.WriteLine("SERVER_INSTANCE_ID=" + endpoint.ServerInstanceId);
            Console.WriteLine("EMBEDDED_NREPL_PORT=" + endpoint.Port);
            while (true)
            {
                var line = Console.ReadLine();
                if (line == null || line.Trim() == "quit")
                    break;
            }
            var stopped = server.StopAsync().GetAwaiter().GetResult();
            Console.WriteLine("EMBEDDED_NREPL_STOP=" + stopped.Status);
            return stopped.IsComplete ? 0 : 3;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

}
