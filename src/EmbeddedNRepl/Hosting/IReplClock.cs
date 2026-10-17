using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EmbeddedNRepl.Hosting;

public interface IReplClock
{
    TimeSpan Elapsed
    {
        get;
    }
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class ReplClock : IReplClock
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    public TimeSpan Elapsed => stopwatch.Elapsed;
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
