using System;
using System.Text;
using EmbeddedNRepl;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Testing;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class RuntimeContractTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase(" \t")]
    public void RuntimeIdentityIsRequired(string? identity)
        => Assert.Throws<ArgumentException>(() => new RuntimeContext(identity!));

    [Test]
    public void OnlyTheCurrentSnapshotWithAnActiveDocumentCanExecute()
    {
        var context = new RuntimeContext("runtime");
        var initial = context.Capture();
        Assert.That(context.IsCurrent(initial), Is.False);
        context.Publish("drawing", "project", 4);
        var captured = context.Capture();
        Assert.That(captured.RuntimeGroupId, Is.EqualTo("runtime"));
        Assert.That(captured.ProjectRevision, Is.EqualTo(4));
        Assert.That(context.IsCurrent(captured), Is.True);
        Assert.That(context.IsCurrent(initial), Is.False);
        context.Publish("drawing", "project", 5);
        Assert.That(context.IsCurrent(captured), Is.False);
        context.Publish(null);
        Assert.That(context.IsCurrent(context.Capture()), Is.False);
    }

    [TestCase("identity")]
    [TestCase("evaluator")]
    [TestCase("sessions")]
    [TestCase("scheduler")]
    [TestCase("host")]
    public void AttachmentRejectsEachMissingDependency(string missing)
    {
        var services = new ServerFixture().Services;
        var error = Assert.Throws<ArgumentNullException>(() => new ReplServices(
            missing == "identity" ? null! : services.Identity,
            missing == "evaluator" ? null! : services.Evaluator,
            missing == "sessions" ? null! : services.Sessions,
            missing == "scheduler" ? null! : services.Scheduler,
            missing == "host" ? null! : services.Host));
        Assert.That(error!.ParamName, Is.EqualTo(missing));
    }

    [TestCase(0, null, null)]
    [TestCase(80, -1, null)]
    [TestCase(80, null, -1)]
    public void InvalidPrinterBoundsAreRejected(int margin, int? length, int? level)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ReplPrintOptions(margin, length, level));

    [Test]
    public void PrinterOptionsClampLargeBoundsAndKeepUnspecifiedLimits()
    {
        var bounded = new ReplPrintOptions(2000, 500, 50);
        Assert.That(bounded.RightMargin, Is.EqualTo(1024));
        Assert.That(bounded.Length, Is.EqualTo(100));
        Assert.That(bounded.Level, Is.EqualTo(10));
        var defaults = new ReplPrintOptions();
        Assert.That(defaults.Length, Is.Null);
        Assert.That(defaults.Level, Is.Null);
    }

    [TestCase("broken-surrogates", "�A�")]
    [TestCase("😀", "😀")]
    [TestCase("Ж漢", "Ж漢")]
    [TestCase(null, "")]
    public void DiagnosticMessagesRemainValidUtf8(string? text, string expected)
    {
        if (text == "broken-surrogates")
            text = "\ud800A\udc00";
        var diagnostic = new ReplEvaluationError("other", text!, text!, text!, text!, text!, 0, -1);
        Assert.That(diagnostic.Message, Is.EqualTo(expected));
        Assert.That(diagnostic.Code, Is.EqualTo("eval-error"));
        Assert.That(diagnostic.Line, Is.EqualTo(1));
        Assert.That(diagnostic.Column, Is.EqualTo(1));
        Assert.DoesNotThrow(() => new UTF8Encoding(false, true).GetBytes(diagnostic.Message));
    }

    [TestCase("PartialFrameTimeout")]
    [TestCase("TimeoutTooLarge")]
    [TestCase("StringBytes")]
    [TestCase("RequestIdBytes")]
    public void InconsistentResourceBoundsAreRejected(string setting)
    {
        var limits = new ReplLimits();
        if (setting == "PartialFrameTimeout")
            limits.PartialFrameTimeout = TimeSpan.Zero;
        else if (setting == "TimeoutTooLarge")
            limits.PartialFrameTimeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1);
        else if (setting == "StringBytes")
            limits.StringBytes = limits.MessageBytes + 1;
        else
            limits.RequestIdBytes = limits.StringBytes + 1;
        Assert.Catch<ArgumentException>(() => limits.Validate());
    }
}
