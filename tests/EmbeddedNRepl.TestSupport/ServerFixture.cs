using System;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl.Testing;

public sealed class ServerFixture
{
    public FakeEvaluator Evaluator { get; } = new FakeEvaluator();
    public FakeStateFactory States { get; } = new FakeStateFactory();
    public ControlledScheduler Scheduler { get; } = new ControlledScheduler();
    public FakeHost Host { get; } = new FakeHost();
    public ReplServices Services
    {
        get;
    }
    public ServerFixture()
    {
        Services = new ReplServices(new ReplHostIdentity("embedded-nrepl-test", "test-build", Guid.NewGuid().ToString(),
            "test-runtime", "standalone-test", "1", "test-host", "test-adapter", "test-1", "fake-no-language"),
            Evaluator, States, Scheduler, Host);
    }
}
