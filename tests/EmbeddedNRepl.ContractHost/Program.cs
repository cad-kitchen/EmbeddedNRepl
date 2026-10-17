using System;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl.ContractHost;

// A compilation probe for consumers without a project, a document, CAD, or a language runtime.
// It deliberately opens no endpoint and performs no evaluation.
internal static class Program
{
    private static void Main()
    {
        var context = new ReplExecutionContext("console-runtime", null, null, null, ReplExecutionMode.Application);
        Type[] ports = { typeof(IReplEvaluator), typeof(IReplSessionStateFactory),
            typeof(IReplExecutionScheduler), typeof(IReplHostContext) };
        Console.WriteLine("Independent contracts: " + ports.Length + "; runtime group: " + context.RuntimeGroupId);
    }
}
