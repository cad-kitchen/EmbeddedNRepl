using System;
using System.Collections.Generic;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Protocol;
using NUnit.Framework;
using static EmbeddedNRepl.Testing.WirePeer;

namespace EmbeddedNRepl.UnitTests;

public sealed class RequestValidationTests
{
    public static IEnumerable<Dictionary<string, object>> InvalidRequests()
    {
        yield return Message();
        yield return Message("op", "");
        yield return Message("op", 1L);
        yield return Message("op", "eval");
        yield return Message("op", "eval", "code", 1L);
        foreach (var field in new[] { "ns", "file", "file-name", "session" })
            yield return Message("op", "eval", "code", "", field, 1L);
        foreach (var field in new[] { "line", "column", "nrepl.middleware.print/quota" })
            foreach (var value in new object[] { 0L, -1L, (long)int.MaxValue + 1, "1" })
                yield return Message("op", "eval", "code", "", field, value);
    }
    [TestCaseSource(nameof(InvalidRequests))]
    public void InvalidFieldsAreRejected(Dictionary<string, object> message)
    {
        var error = Assert.Throws<ReplRequestException>(() => ReplRequest.Parse(message, new ReplLimits()));
        Assert.That(error!.Code, Is.EqualTo("embedded-nrepl/invalid-request"));
    }
    [TestCase("eval")]
    [TestCase("reader")]
    [TestCase("readers")]
    [TestCase("print-fn")]
    [TestCase("nrepl.middleware.print/print")]
    [TestCase("nrepl.middleware.print/options")]
    [TestCase("nrepl.middleware.caught/caught")]
    [TestCase("cider.nrepl/print-options")]
    public void ExecutionChangingOptionsAreRejected(string field)
    {
        var error = Assert.Throws<ReplRequestException>(() => ReplRequest.Parse(
            Message("op", "eval", "code", "(side-effect)", field, "custom/function"), new ReplLimits()));
        Assert.That(error!.Code, Is.EqualTo("embedded-nrepl/unsupported-option"));
    }
    [Test]
    public void EvalFieldsPreserveUnicodeEmptySourceAndHarmlessMetadata()
    {
        var request = ReplRequest.Parse(Message("op", "eval", "id", "я😀", "session", "original",
            "code", "(str \"я😀\")", "ns", "my.ns", "file", "buffer.clj", "line", (long)int.MaxValue,
            "column", 7L, "nrepl.middleware.print/quota", 32L, "editor-note", "ignored"), new ReplLimits());
        Assert.That(request.Id, Is.EqualTo("я😀"));
        Assert.That(request.Session, Is.EqualTo("original"));
        var evaluation = request.Evaluation!;
        Assert.That(evaluation.Source, Is.EqualTo("(str \"я😀\")"));
        Assert.That(evaluation.Namespace, Is.EqualTo("my.ns"));
        Assert.That(evaluation.SourceName, Is.EqualTo("buffer.clj"));
        Assert.That(evaluation.Line, Is.EqualTo(int.MaxValue));
        Assert.That(evaluation.Column, Is.EqualTo(7));
        Assert.That(evaluation.ValueQuotaBytes, Is.EqualTo(32));
        var empty = ReplRequest.Parse(Message("op", "eval", "code", "", "nrepl.middleware.print/quota", 999999L), new ReplLimits());
        Assert.That(empty.Evaluation!.Source, Is.Empty);
        Assert.That(empty.Evaluation.ValueQuotaBytes, Is.EqualTo(65536));
        Assert.That(empty.Id, Is.Null);
    }
    [Test]
    public void PrettyPrinterOptionsAreValidatedAndCannotIncreaseServerLimits()
    {
        ReplRequest Parse(Dictionary<string, object> options) => ReplRequest.Parse(Message("op", "eval", "code", "(+ 2 3)",
            "nrepl.middleware.print/print", "cider.nrepl.pprint/pprint", "nrepl.middleware.print/options", options), new ReplLimits());
        var printing = Parse(Message("right-margin", 20000L, "length", 20000L, "level", 20000L)).Evaluation!.Printing!;
        Assert.That(printing.RightMargin, Is.EqualTo(1024));
        Assert.That(printing.Length, Is.EqualTo(100));
        Assert.That(printing.Level, Is.EqualTo(10));
        Assert.That(Parse(Message("length", 0L, "level", 0L)).Evaluation!.Printing!.Length, Is.Zero);
        foreach (var field in new[] { "right-margin", "length", "level" })
            foreach (var value in new object[] { -1L, (long)int.MaxValue + 1, "50" })
                Assert.That(Assert.Throws<ReplRequestException>(() => Parse(Message(field, value)))!.Code, Is.EqualTo("embedded-nrepl/invalid-request"));
        Assert.That(Assert.Throws<ReplRequestException>(() => Parse(Message("right-margin", 0L)))!.Code, Is.EqualTo("embedded-nrepl/invalid-request"));
        Assert.That(Assert.Throws<ReplRequestException>(() => Parse(Message("stream", "custom")))!.Code, Is.EqualTo("embedded-nrepl/unsupported-option"));
    }

    [Test]
    public void UnusableIdsCloseWithoutAllocatingAnOversizedResponse()
    {
        foreach (var id in new object[] { 1L, new string('я', 129) })
        {
            var error = Assert.Throws<ReplRequestException>(() => ReplRequest.Parse(Message("op", "describe", "id", id), new ReplLimits()));
            Assert.That(error!.CloseConnection, Is.True);
        }
        Assert.That(ReplRequest.Parse(Message("op", "describe", "id", new string('я', 128)), new ReplLimits()).Id,
            Is.EqualTo(new string('я', 128)));
    }
}
