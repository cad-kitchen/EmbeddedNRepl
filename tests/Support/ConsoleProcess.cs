using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbeddedNRepl.Testing;

internal sealed class ConsoleProcess : IDisposable
{
    private const string PortPrefix = "EMBEDDED_NREPL_PORT=";
    private readonly Process process;
    private readonly Task<string> stderr;
    private Task<string>? stdout;
    public int Port
    {
        get; private set;
    }
    public string Runtime { get; private set; } = "";
    private ConsoleProcess(Process process)
    {
        this.process = process;
        stderr = process.StandardError.ReadToEndAsync();
    }
    public static async Task<ConsoleProcess> Start(int port = 0)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "console-host", "EmbeddedNRepl.ConsoleHost.exe");
        var start = new ProcessStartInfo(path, "--port " + port)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
#if !NETFRAMEWORK
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        start.EnvironmentVariables["DOTNET_ROOT"] = root;
        start.EnvironmentVariables["DOTNET_ROOT_X64"] = root;
#endif
        var result = new ConsoleProcess(Process.Start(start)!);
        try
        {
            while (true)
            {
                var read = result.process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(30000)) != read)
                    throw new TimeoutException("Console host did not become ready.");
                var line = await read;
                if (line == null)
                    throw new InvalidOperationException("Console host exited before readiness: " + await result.stderr);
                TestContext.Progress.WriteLine(line);
                if (line.StartsWith("ACTUAL_RUNTIME=", StringComparison.Ordinal))
                    result.Runtime = line.Substring(15);
                if (line.StartsWith(PortPrefix, StringComparison.Ordinal))
                {
                    result.Port = int.Parse(line.Substring(PortPrefix.Length));
                    result.stdout = result.process.StandardOutput.ReadToEndAsync();
                    return result;
                }
            }
        }
        catch { result.Dispose(); throw; }
    }
    public async Task Stop()
    {
        if (!process.HasExited)
        {
            await process.StandardInput.WriteLineAsync("quit");
            process.StandardInput.Close();
        }
        if (!process.WaitForExit(10000))
            throw new TimeoutException("Console host failed to stop.");
        var output = stdout == null ? "" : await stdout;
        var error = await stderr;
        Assert.That(process.ExitCode, Is.Zero, output + error);
        Assert.That(output, Does.Contain("EMBEDDED_NREPL_STOP=stopped"));
    }
    public void Dispose()
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit(5000);
        }
        process.Dispose();
    }
}
