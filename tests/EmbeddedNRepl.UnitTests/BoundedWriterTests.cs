using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EmbeddedNRepl.Hosting;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class BoundedWriterTests
{
    [TestCase("ascii", "ascii")]
    [TestCase("КЖ漢", "КЖ漢")]
    [TestCase("😀", "😀")]
    [TestCase("high-then-ascii", "�A")]
    [TestCase("lone-low", "�")]
    [TestCase("lone-high", "�")]
    public void BothWritersPreserveUnicodeAndReplaceIncompleteSurrogates(string input, string expected)
    {
        input = input switch
        {
            "high-then-ascii" => "\ud800A",
            "lone-low" => "\udc00",
            "lone-high" => "\ud800",
            _ => input
        };
        var sink = new Sink();
        using (var writer = new ReplOutputTextWriter(sink, ReplOutputKind.Error, new object()))
        {
            foreach (var character in input)
                writer.Write(character);
            writer.Flush();
            Assert.That(writer.Encoding, Is.SameAs(Encoding.UTF8));
        }
        Assert.That(sink.Text, Is.EqualTo(expected));
        Assert.That(sink.Kinds, Is.All.EqualTo(ReplOutputKind.Error));
        using var quota = new Utf8QuotaWriter(64);
        quota.Write(input);
        quota.Finish();
        quota.Finish();
        Assert.That(quota.ToString(), Is.EqualTo(expected));
        Assert.That(quota.Encoding, Is.SameAs(Encoding.UTF8));
        Assert.DoesNotThrow(() => new UTF8Encoding(false, true).GetBytes(sink.Text));
    }

    [Test]
    public void StringAndArrayWritesUseOnlyTheRequestedSliceAndBoundEveryChunk()
    {
        var sink = new Sink();
        using var writer = new ReplOutputTextWriter(sink, ReplOutputKind.Out, new object());
        writer.Write((string?)null);
        writer.Write("");
        writer.Write(new[] { 'x', 'К', 'Ж', 'y' }, 1, 2);
        writer.Write(new string('a', 4093) + "😀" + new string('b', 5000));
        writer.Flush();
        Assert.That(sink.Text, Is.EqualTo("КЖ" + new string('a', 4093) + "😀" + new string('b', 5000)));
        Assert.That(sink.Chunks, Is.All.Matches<string>(chunk => chunk.Length <= 4095));
        Assert.That(sink.Flushes, Is.EqualTo(1));
        foreach (var chunk in sink.Chunks)
            Assert.DoesNotThrow(() => new UTF8Encoding(false, true).GetBytes(chunk));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectedOutputStopsTheCurrentWriteAndDoesNotRetry(bool useArray)
    {
        var sink = new Sink { Accept = false };
        using var writer = new ReplOutputTextWriter(sink, ReplOutputKind.Out, new object());
        var input = new string('x', 9000);
        if (useArray)
            writer.Write(input.ToCharArray(), 0, input.Length);
        else
            writer.Write(input);
        writer.Write('z');
        writer.Write("later");
        writer.Flush();
        Assert.That(sink.Attempts, Is.EqualTo(1));
        Assert.That(sink.Text, Is.Empty);
    }

    [Test]
    public void DisposalDetachesTheSinkAndCountsLateCharactersWithoutForwardingThem()
    {
        var sink = new Sink();
        var writer = new ReplOutputTextWriter(sink, ReplOutputKind.Out, new object());
        writer.Write("before");
        writer.Dispose();
        writer.Dispose();
        writer.Write('a');
        writer.Write("bc");
        writer.Write(new[] { 'd', 'e', 'f' }, 1, 2);
        writer.Flush();
        Assert.That(sink.Text, Is.EqualTo("before"));
        Assert.That(sink.Late, Is.EqualTo(5));
        Assert.That(sink.Flushes, Is.Zero);
        var simple = new SimpleSink();
        var ordinaryWriter = new ReplOutputTextWriter(simple, ReplOutputKind.Out, new object());
        ordinaryWriter.Dispose();
        Assert.DoesNotThrow(() => ordinaryWriter.Write("late"));
        Assert.That(simple.Calls, Is.Zero);
    }

    [TestCase(-1, 0)]
    [TestCase(0, -1)]
    [TestCase(2, 2)]
    [TestCase(4, 0)]
    public void InvalidArraySlicesAreRejectedBeforeAnyOutput(int offset, int count)
    {
        var sink = new Sink();
        using var writer = new ReplOutputTextWriter(sink, ReplOutputKind.Out, new object());
        using var quota = new Utf8QuotaWriter(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Write(new char[3], offset, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => quota.Write(new char[3], offset, count));
        Assert.That(sink.Attempts, Is.Zero);
        Assert.That(quota.ToString(), Is.Empty);
    }

    [Test]
    public void NullBuffersAndWriterDependenciesAreRejected()
    {
        using var writer = new ReplOutputTextWriter(new Sink(), ReplOutputKind.Out, new object());
        using var quota = new Utf8QuotaWriter(10);
        Assert.Throws<ArgumentNullException>(() => writer.Write(null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => quota.Write(null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => new ReplOutputTextWriter(null!, ReplOutputKind.Out, new object()));
        Assert.Throws<ArgumentNullException>(() => new ReplOutputTextWriter(new Sink(), ReplOutputKind.Out, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Utf8QuotaWriter(0));
    }

    [Test]
    public void Utf8QuotaCountsBytesAndStaysExceededAfterRejectingACharacter()
    {
        using var writer = new Utf8QuotaWriter(6);
        writer.Write((string?)null);
        writer.Write("AЖ".ToCharArray(), 0, 2);
        writer.Write('漢');
        Assert.That(writer.ToString(), Is.EqualTo("AЖ漢"));
        Assert.That(writer.Exceeded, Is.False);
        Assert.Throws<Utf8QuotaExceededException>(() => writer.Write('x'));
        Assert.That(writer.Exceeded, Is.True);
        Assert.Throws<Utf8QuotaExceededException>(() => writer.Write('y'));
        Assert.That(writer.ToString(), Is.EqualTo("AЖ漢"));
        using var unfinished = new Utf8QuotaWriter(2);
        unfinished.Write('\ud800');
        Assert.Throws<Utf8QuotaExceededException>(() => unfinished.Finish());
        Assert.That(unfinished.Exceeded, Is.True);
    }

    private sealed class SimpleSink : IReplOutput
    {
        public int Calls;
        public bool TryWrite(ReplOutputKind kind, string text, int offset, int count)
        {
            Calls++;
            return true;
        }
        public void Flush()
        {
        }
    }

    private sealed class Sink : IReplEvaluationOutput
    {
        public readonly List<string> Chunks = new List<string>();
        public readonly List<ReplOutputKind> Kinds = new List<ReplOutputKind>();
        public bool Accept = true;
        public int Attempts, Flushes, Late;
        public string Text => string.Concat(Chunks);
        public Action<int>? LateOutput => count => Late += count;
        public bool TryWrite(ReplOutputKind kind, string text, int offset, int count)
        {
            Attempts++;
            if (!Accept)
                return false;
            Chunks.Add(text.Substring(offset, count));
            Kinds.Add(kind);
            return true;
        }
        public void Flush() => Flushes++;
        public bool TryWriteValue(string value, string ns, bool truncated) => throw new NotSupportedException();
        public void ReportError(ReplEvaluationError error) => throw new NotSupportedException();
        public void MarkTruncated() => throw new NotSupportedException();
    }
}
