using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using EmbeddedNRepl.Hosting;
using clojure.lang;
using clojure.lang.Runtime;
using Timer = System.Windows.Forms.Timer;

namespace EmbeddedNRepl.OfficeAddIn;

internal sealed class OfficeReplHost : IDisposable
{
    private readonly string product;
    private readonly string portFile;
    private readonly RuntimeCoordinator scheduler = new RuntimeCoordinator();
    private readonly ApplicationReplContext context;
    private readonly OfficeApi api;
    private readonly Timer timer;
    private ReplServer? server;
    private bool disposed;

    internal OfficeReplHost(string product, object application)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Office must connect the add-in on its main STA thread.");
        this.product = product;
        var group = Guid.NewGuid().ToString();
        context = new ApplicationReplContext(group);
        api = product == "Word" ? (OfficeApi)new WordApi(application) : new ExcelApi(application);
        timer = new Timer { Interval = 25 };
        portFile = Path.Combine(StateDirectory, product.ToLowerInvariant() + "-" + Process.GetCurrentProcess().Id + ".port");
        try
        {
            RT.var("clojure.core", "eval").invoke(RT.readString("(+ 1 2)"));
            var frame = Var.getThreadBindingFrame();
            try
            {
                var user = Namespace.findOrCreate(Symbol.intern("user"));
                Var.pushThreadBindings(RT.map(RT.CurrentNSVar, user));
                RT.var("clojure.core", "refer").invoke(Symbol.intern("clojure.core"));
                var sampleNamespace = product.ToLowerInvariant();
                Var.intern(Namespace.findOrCreate(Symbol.intern(sampleNamespace)), Symbol.intern("host"), api);
                using (var stream = typeof(OfficeReplHost).Assembly.GetManifestResourceStream(sampleNamespace + ".cljr"))
                using (var reader = new StreamReader(stream!))
                    RT.var("clojure.core", "load-string").invoke(reader.ReadToEnd());
            }
            finally { Var.resetThreadBindingFrame(frame); }
            var adapter = ClojureAdapter.Attach(group, source => RT.var("clojure.core", "load-string").invoke(source));
            var version = typeof(OfficeReplHost).Assembly.GetName().Version!.ToString();
            var officeVersion = (string)((dynamic)application).Version;
            var languageVersion = (string)RT.var("clojure.core", "clojure-version").invoke();
            var identity = new ReplHostIdentity("embedded-nrepl-" + product.ToLowerInvariant(), version,
                Guid.NewGuid().ToString(), group, "Microsoft " + product, officeVersion, officeVersion,
                ".NETFramework,Version=v4.7.2", version, languageVersion);
            server = new ReplServer(new ReplServices(identity, adapter.Evaluator, adapter.Sessions, scheduler, context));
            var endpoint = server.Start(true, new IPEndPoint(IPAddress.Loopback, 0))!;
            timer.Tick += Pump;
            timer.Start();
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(portFile, endpoint.Port.ToString(CultureInfo.InvariantCulture));
            Log(product, "Listening on 127.0.0.1:" + endpoint.Port + "; owner thread " + api.OwnerThreadId);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private async void Pump(object? sender, EventArgs args)
    {
        if (disposed || scheduler.IsBusy || !scheduler.HasPendingWork)
            return;
        try
        {
            api.CheckAccess();
            // ClojureAdapter evaluates and prints synchronously here, on the Office UI thread.
            await scheduler.ExecuteNextAsync(callback => callback());
        }
        catch (Exception error) { Log(product, error.ToString()); }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        timer.Stop();
        timer.Dispose();
        context.Invalidate();
        scheduler.Dispose();
        api.Detach();
        // StopAsync closes transport without waiting on the UI pump. The runtime stays alive.
        var stopped = server?.StopAsync().GetAwaiter().GetResult();
        if (File.Exists(portFile))
            File.Delete(portFile);
        Log(product, "Stopped: " + (stopped?.Status ?? "startup incomplete"));
    }

    internal static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbeddedNRepl", "Office");

    internal static void Log(string product, string message)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            File.AppendAllText(Path.Combine(StateDirectory, product.ToLowerInvariant() + "-" + Process.GetCurrentProcess().Id + ".log"),
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
