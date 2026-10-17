using System;
using System.Collections.Generic;
using System.Text;
using EmbeddedNRepl.Protocol;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class BencodeBudgetTests
{
    private static BencodeFrame? Read(BencodeDecoder decoder, string wire)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(wire);
        return decoder.Read(bytes, 0, bytes.Length, out _);
    }

    [Test]
    public void DeclaredStringMustFitTheRemainingFrameBeforeItsBodyArrives()
    {
        var limits = new ReplLimits { MessageBytes = 32, StringBytes = 32, RequestIdBytes = 8 };
        var decoder = new BencodeDecoder(limits);
        Assert.Throws<BencodeException>(() => Read(decoder, "d1:x25:"));
    }

    [Test]
    public void OversizedHeaderIsRejectedWithoutReceivingTheBody()
        => Assert.Throws<BencodeException>(() => Read(new BencodeDecoder(new ReplLimits()), "d1:x1048577:"));

    [Test]
    public void TotalMessageBudgetIncludesTheEnvelopeAndClosingTokens()
    {
        var limits = new ReplLimits { MessageBytes = 8, StringBytes = 8, RequestIdBytes = 1 };
        Assert.That(Read(new BencodeDecoder(limits), "d1:a1:xe")!.SizeBytes, Is.EqualTo(8));
        Assert.Throws<BencodeException>(() => Read(new BencodeDecoder(limits), "d1:a2:xxe"));
    }

    [Test]
    public void ValuesIncludeDictionaryKeysAndContainers()
    {
        var limits = new ReplLimits { DecodedValues = 4 };
        Assert.Throws<BencodeException>(() => Read(new BencodeDecoder(limits), "d1:ai1e1:bi2ee"));
        limits.DecodedValues = 5;
        Assert.That(Read(new BencodeDecoder(limits), "d1:ai1e1:bi2ee"), Is.Not.Null);
    }

    [Test]
    public void DepthIncludesTheRootDictionary()
    {
        var limits = new ReplLimits { ContainerDepth = 2 };
        Assert.That(Read(new BencodeDecoder(limits), "d1:alee"), Is.Not.Null);
        Assert.Throws<BencodeException>(() => Read(new BencodeDecoder(limits), "d1:alleee"));
    }

    [Test]
    public void NumericTokenBudgetIncludesTheSignAndLengthHeaders()
    {
        var limits = new ReplLimits { NumericTokenBytes = 1 };
        Assert.Throws<BencodeException>(() => Read(new BencodeDecoder(limits), "d1:ai-1ee"));
        Assert.Throws<BencodeException>(() => Read(new BencodeDecoder(limits), "d1:a10:"));
        limits.NumericTokenBytes = 2;
        Assert.That(Read(new BencodeDecoder(limits), "d1:ai-1ee"), Is.Not.Null);
        Assert.That(Read(new BencodeDecoder(limits), "d1:a10:0123456789e"), Is.Not.Null);
    }

    [Test]
    public void ConfigurationMutationCannotRelaxAnExistingDecoderBudget()
    {
        var limits = new ReplLimits { ContainerDepth = 1 };
        var decoder = new BencodeDecoder(limits);
        limits.ContainerDepth = 100;
        Assert.Throws<BencodeException>(() => Read(decoder, "d1:alee"));
    }

    [Test]
    public void AProtocolFailurePermanentlyInvalidatesTheDecoder()
    {
        var decoder = new BencodeDecoder(new ReplLimits());
        Assert.Throws<BencodeException>(() => Read(decoder, "x"));
        Assert.Throws<BencodeException>(() => Read(decoder, "de"));
    }

    [Test]
    public void FrameBudgetsResetAfterEveryCompleteMessage()
    {
        var limits = new ReplLimits { MessageBytes = 2, StringBytes = 2, RequestIdBytes = 1, DecodedValues = 1 };
        var decoder = new BencodeDecoder(limits);
        for (int i = 0; i < 1000; i++)
            Assert.That(Read(decoder, "de")!.SizeBytes, Is.EqualTo(2));
    }

    [Test]
    public void SeededMalformedIntegerTokensAlwaysFail()
    {
        var random = new Random(1729);
        for (int i = 0; i < 1000; i++)
        {
            string wire = "d1:xi" + random.Next(1000000) + "x" + random.Next(1000000) + "ee";
            var decoder = new BencodeDecoder(new ReplLimits());
            Assert.Throws<BencodeException>(() => Read(decoder, wire), "seed=1729, case=" + i + ", wire=" + wire);
        }
    }
}
