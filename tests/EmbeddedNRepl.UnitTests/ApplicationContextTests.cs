using System;
using EmbeddedNRepl.Hosting;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class ApplicationContextTests
{
    [Test]
    public void ApplicationSnapshotsWorkWithoutDocumentsAndRejectReplacedOrInvalidatedState()
    {
        var state = new object();
        var context = new ApplicationReplContext("application", state);
        var first = context.Capture();
        Assert.That(context.IsCurrent(first), Is.True);
        Assert.That(first.HostState, Is.SameAs(state));
        Assert.That(first.DocumentId, Is.Null);
        Assert.That(first.Mode, Is.EqualTo(ReplExecutionMode.Application));
        context.Publish(state);
        Assert.That(context.IsCurrent(first), Is.False);
        Assert.That(context.IsCurrent(new ReplExecutionContext("application", state)), Is.False);
        var second = context.Capture();
        context.Invalidate();
        Assert.That(context.IsCurrent(second), Is.False);
        context.Publish();
        Assert.That(context.IsCurrent(second), Is.False);
        Assert.That(context.IsCurrent(context.Capture()), Is.True);
        Assert.That(context.Capture().HostState, Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" \t")]
    public void ApplicationIdentityIsRequired(string? identity)
        => Assert.Throws<ArgumentException>(() => new ApplicationReplContext(identity!));

    [Test]
    public void AttachmentValidatesArgumentsBeforeCallingTheHost()
    {
        Func<string, object> forbidden = _ => throw new Exception("Must not call the host.");
        Assert.Throws<ArgumentException>(() => ClojureAdapter.Attach("", forbidden));
        Assert.Throws<ArgumentNullException>(() => ClojureAdapter.Attach("runtime", null!));
        Assert.Throws<ArgumentException>(() => ClojureAdapter.Attach("runtime", forbidden, " "));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void AttachmentRejectsInvalidHostResults(int resultKind)
    {
        var services = new Testing.ServerFixture().Services;
        object result = resultKind == 0 ? new object() : resultKind == 1 ? new object[0] :
            resultKind == 2 ? new object[] { new object(), services.Sessions } :
            new object[] { services.Evaluator, new object() };
        Assert.Throws<InvalidOperationException>(() => ClojureAdapter.Attach("runtime", _ => result));
    }
}
