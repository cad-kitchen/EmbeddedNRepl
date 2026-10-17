using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbeddedNRepl.Testing;

public static class TestDeadline
{
    public static async Task Complete(Task task)
    {
        using (var cancel = new CancellationTokenSource())
        {
            try
            {
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), cancel.Token)).ConfigureAwait(false) != task)
                    throw new TimeoutException("Test harness made no progress within its watchdog deadline.");
                await task.ConfigureAwait(false);
            }
            finally { cancel.Cancel(); }
        }
    }
    public static async Task<T> Complete<T>(Task<T> task)
    {
        await Complete((Task)task).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }
}
