using System;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl.ConsoleHost;

// Compatibility wrapper for the original console sample.
public sealed class ConsoleRuntimeScheduler : IReplExecutionScheduler, IDisposable
{
    private readonly DedicatedThreadScheduler scheduler;
    public ConsoleRuntimeScheduler(Action initialize) => scheduler = new DedicatedThreadScheduler(initialize);
    public Task Completion => scheduler.Completion;
    public IReplScheduledOperation Schedule(Guid id, ReplExecutionContext context, Func<CancellationToken, Task> callback)
        => scheduler.Schedule(id, context, callback);
    public void Dispose() => scheduler.Dispose();
}
