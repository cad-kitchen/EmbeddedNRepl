using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using EmbeddedNRepl;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Testing;
using clojure.lang;

internal static class Program
{
    private static async Task<int> Main()
    {
        const string group = "independent-package-consumer";
        ClojureAdapter? adapter = null;
        using var scheduler = new DedicatedThreadScheduler(() =>
        {
            RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)"));
            adapter = ClojureAdapter.Attach(group,
                source => RT.var("clojure.core", "load-string").invoke(source), "clojure.core");
        });
        var identity = new ReplHostIdentity("package-consumer", "1", Guid.NewGuid().ToString(), group,
            "console", "1", "1", "independent", "1", "1.12.3");
        var context = new ApplicationReplContext(group);
        var server = new ReplServer(new ReplServices(identity, adapter!.Evaluator, adapter.Sessions, scheduler, context));
        try
        {
            var endpoint = server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!;
            if (endpoint.Profile != "embedded-nrepl-clojureclr-v1")
                throw new InvalidOperationException("Unexpected package endpoint profile.");
            using (var peer = await WirePeer.Connect(endpoint))
            {
                await peer.Send(WirePeer.Message("op", "describe", "id", "identity"));
                var description = await peer.Receive();
                var versions = (Dictionary<string, object>)description["versions"];
                var host = (Dictionary<string, object>)description["aux"];
                if (!versions.ContainsKey("embedded-nrepl") || !Equals(host["embedded-nrepl/host-product"], "console"))
                    throw new InvalidOperationException("Package identity does not match the embedded protocol.");
                await peer.Send(WirePeer.Message("op", "eval", "id", "package", "code", "(+ 1 2)"));
                var valueSeen = false;
                while (true)
                {
                    var response = await peer.Receive();
                    if (response.TryGetValue("value", out var value) && Equals(value, "3")) valueSeen = true;
                    if (response.TryGetValue("status", out var status) && ((IEnumerable<object>)status).Contains("done"))
                    {
                        if (!valueSeen || ((IEnumerable<object>)status).Contains("eval-error"))
                            throw new InvalidOperationException("Package evaluation did not return 3 successfully.");
                        break;
                    }
                }
            }
            if (!(await server.StopAsync()).IsComplete) throw new InvalidOperationException("Server stop was incomplete.");
            object? afterStop = null;
            var operation = scheduler.Schedule(Guid.NewGuid(), context.Capture(), _ =>
            {
                afterStop = RT.var("clojure.core", "eval").invoke(RT.readString("(+ 2 3)"));
                return Task.CompletedTask;
            });
            await TestDeadline.Complete(operation.Completion);
            if (!Equals(afterStop, 5L)) throw new InvalidOperationException("Host runtime stopped with nREPL.");
            Console.WriteLine("PACKAGE_CONSUMER=passed; eval=3; runtime-after-stop=5");
            return 0;
        }
        finally
        {
            await server.StopAsync();
            scheduler.Dispose();
            await TestDeadline.Complete(scheduler.Completion);
        }
    }
}
