using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using EmbeddedNRepl.Testing;
using NUnit.Framework;
using static EmbeddedNRepl.Testing.WirePeer;

namespace EmbeddedNRepl.ClientTests;

[NonParallelizable]
public sealed class InstalledClientTests
{
    private string directory = "";
    private string client = "";
    [SetUp]
    public void Configure()
    {
        directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "client-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        client = Environment.GetEnvironmentVariable("EMBEDDED_NREPL_CLIENT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "clj-nrepl-eval");
        if (!File.Exists(client))
            Assert.Inconclusive("Required installed clj-nrepl-eval script is unavailable: " + client);
        using var hash = SHA256.Create();
        using var stream = File.OpenRead(client);
        TestContext.Progress.WriteLine("CLIENT_PATH=" + client);
        TestContext.Progress.WriteLine("CLIENT_SHA256=" + BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
    }
    private async Task<(string Output, string Error)> Eval(int port, string code, int timeout = 10000)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("EMBEDDED_NREPL_BB") ?? "bb.exe",
            "-f \"" + client + "\" -- --host 127.0.0.1 --port " + port + " --timeout " + timeout)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.EnvironmentVariables["XDG_RUNTIME_DIR"] = directory;
        var bbDirectory = Path.GetDirectoryName(start.FileName);
        if (!string.IsNullOrEmpty(bbDirectory))
            start.EnvironmentVariables["PATH"] = bbDirectory + Path.PathSeparator + start.EnvironmentVariables["PATH"];
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            var sourceBytes = Encoding.UTF8.GetBytes(code);
            await process.StandardInput.BaseStream.WriteAsync(sourceBytes, 0, sourceBytes.Length);
            process.StandardInput.Close();
            if (!process.WaitForExit(30000))
                Assert.Fail("Installed client exceeded its outer 30-second deadline.");
            var result = (await output, await error);
            TestContext.Progress.WriteLine("CLIENT_STDOUT=" + result.Item1);
            TestContext.Progress.WriteLine("CLIENT_STDERR=" + result.Item2);
            Assert.That(process.ExitCode, Is.Zero, result.Item1 + result.Item2);
            return result;
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    }
    [Test]
    public async Task InstalledClientEvaluatesReusesSessionRecoversAfterErrorAndReconnectsAfterRestart()
    {
        int port;
        object instance;
        using (var host = await ConsoleProcess.Start())
        {
            port = host.Port;
            using var peer = await Connect(IPAddress.Loopback, port);
            await peer.Send(Message("op", "describe", "id", "describe"));
            var description = await peer.Receive();
            var aux = (System.Collections.Generic.Dictionary<string, object>)description["aux"];
            instance = aux["serverInstanceId"];
            Assert.That(aux["productId"], Is.EqualTo("embedded-nrepl-console"));
            var first = await Eval(port, "(def answer 41) (+ answer 1) (println \"hello client Привет 😀\")");
            Assert.That(first.Output, Does.Contain("=> 42").And.Contain("hello client Привет 😀"));
            var reused = await Eval(port, "(inc answer)");
            Assert.That(reused.Output, Does.Contain("=> 42"));
            var failed = await Eval(port, "(println \"before error\") (throw (System.Exception. \"client failure\")) (def forbidden true)");
            Assert.That(failed.Output, Does.Contain("before error"));
            Assert.That(failed.Error, Does.Contain("client failure"));
            var recovery = await Eval(port, "[(inc answer) (some? *e) (nil? (resolve 'forbidden))]");
            Assert.That(recovery.Output, Does.Contain("=> [42 true true]"));
            await peer.Send(Message("op", "ls-sessions", "id", "sessions"));
            Assert.That((await peer.Receive())["sessions"], Has.Count.EqualTo(1));
            await host.Stop();
        }
        using (var restarted = await ConsoleProcess.Start(port))
        {
            using var peer = await Connect(IPAddress.Loopback, port);
            await peer.Send(Message("op", "describe", "id", "describe"));
            var aux = (System.Collections.Generic.Dictionary<string, object>)(await peer.Receive())["aux"];
            Assert.That(aux["serverInstanceId"], Is.Not.EqualTo(instance));
            Assert.That((await Eval(port, "(+ 20 22)")).Output, Does.Contain("=> 42"));
            await restarted.Stop();
        }
    }
}
