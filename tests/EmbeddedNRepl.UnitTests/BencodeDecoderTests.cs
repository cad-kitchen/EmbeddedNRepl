using System;
using System.Collections.Generic;
using System.Text;
using EmbeddedNRepl.Protocol;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class BencodeDecoderTests
{
    [TestCase("d2:op4:eval2:op8:describee")]
    [TestCase("d1:ai00ee")]
    [TestCase("d1:ai-0ee")]
    [TestCase("d01:a0:e")]
    [TestCase("d1:ai9223372036854775808ee")]
    [TestCase("d1:ai-9223372036854775809ee")]
    [TestCase("d1:a99999999999999999999:")]
    [TestCase("d1:ai+1ee")]
    [TestCase("d1:aiee")]
    [TestCase("d1:ai-ee")]
    [TestCase("d1:aixe")]
    [TestCase("d1:ae")]
    [TestCase("di1e0:e")]
    [TestCase("le")]
    [TestCase("0:")]
    [TestCase("i1e")]
    public void MalformedFramesAreRejected(string wire)
    {
        var decoder = new BencodeDecoder(new ReplLimits());
        byte[] bytes = Encoding.UTF8.GetBytes(wire);
        Assert.Throws<BencodeException>(() => decoder.Read(bytes, 0, bytes.Length, out _));
    }

    [TestCase(new byte[] { 0xc0, 0xaf })]
    [TestCase(new byte[] { 0xed, 0xa0, 0x80 })]
    [TestCase(new byte[] { 0xf4, 0x90, 0x80, 0x80 })]
    public void InvalidUtf8TextIsRejected(byte[] invalid)
    {
        var wire = new List<byte>(Encoding.ASCII.GetBytes("d1:x" + invalid.Length + ":"));
        wire.AddRange(invalid);
        wire.Add((byte)'e');
        var decoder = new BencodeDecoder(new ReplLimits());
        Assert.Throws<BencodeException>(() => decoder.Read(wire.ToArray(), 0, wire.Count, out _));
    }

    [TestCase("d")]
    [TestCase("d1:x")]
    [TestCase("d1:x3:a")]
    [TestCase("d1:xi12")]
    [TestCase("d1:xle")]
    public void EndOfStreamDoesNotCompleteAPartialMessage(string wire)
    {
        var decoder = new BencodeDecoder(new ReplLimits());
        byte[] bytes = Encoding.UTF8.GetBytes(wire);
        Assert.That(decoder.Read(bytes, 0, bytes.Length, out _), Is.Null);
        Assert.That(decoder.HasPartialFrame, Is.True);
        Assert.Throws<BencodeException>(decoder.Complete);
    }

    [Test]
    public void EverySplitAndOneByteFragmentsPreserveThreeCoalescedFrames()
    {
        byte[] wire = Encoding.UTF8.GetBytes("d2:op4:eval4:code9:Ж中🙂ede" + "d2:op8:describee");
        for (int split = 0; split <= wire.Length; split++)
        {
            var decoder = new BencodeDecoder(new ReplLimits());
            var frames = new List<BencodeFrame>();
            ReadChunk(decoder, wire, 0, split, frames);
            ReadChunk(decoder, wire, split, wire.Length - split, frames);
            AssertFrames(decoder, frames);
        }
        var fragmented = new BencodeDecoder(new ReplLimits());
        var byteFrames = new List<BencodeFrame>();
        for (int i = 0; i < wire.Length; i++)
            ReadChunk(fragmented, wire, i, 1, byteFrames);
        AssertFrames(fragmented, byteFrames);
    }

    private static void ReadChunk(BencodeDecoder decoder, byte[] bytes, int offset, int count, List<BencodeFrame> frames)
    {
        while (count > 0)
        {
            var frame = decoder.Read(bytes, offset, count, out int consumed);
            Assert.That(consumed, Is.GreaterThan(0));
            if (frame != null)
                frames.Add(frame);
            offset += consumed;
            count -= consumed;
        }
    }

    private static void AssertFrames(BencodeDecoder decoder, List<BencodeFrame> frames)
    {
        Assert.That(frames.Count, Is.EqualTo(3));
        Assert.That(frames[0].Message["code"], Is.EqualTo("Ж中🙂"));
        Assert.That(frames[1].Message, Is.Empty);
        Assert.That(frames[2].Message["op"], Is.EqualTo("describe"));
        decoder.Complete();
    }

    private static BencodeFrame ReadOne(string wire)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(wire);
        var decoder = new BencodeDecoder(new ReplLimits());
        var frame = decoder.Read(bytes, 0, bytes.Length, out int consumed);
        Assert.That(frame, Is.Not.Null);
        Assert.That(consumed, Is.EqualTo(bytes.Length));
        Assert.That(frame!.SizeBytes, Is.EqualTo(bytes.Length));
        Assert.That(decoder.HasPartialFrame, Is.False);
        decoder.Complete();
        return frame;
    }

    [TestCase("0", 0L)]
    [TestCase("1", 1L)]
    [TestCase("-1", -1L)]
    [TestCase("9223372036854775807", long.MaxValue)]
    [TestCase("-9223372036854775808", long.MinValue)]
    public void ReadsSigned64BitIntegers(string number, long expected)
        => Assert.That(ReadOne("d1:ni" + number + "ee").Message["n"], Is.EqualTo(expected));

    [Test]
    public void ReadsTextUsingUtf8ByteLengths()
        => Assert.That(ReadOne("d4:code9:Ж中🙂e").Message["code"], Is.EqualTo("Ж中🙂"));

    [Test]
    public void ReadsEmptyStringsAndNestedContainersWithUnsortedKeys()
    {
        var frame = ReadOne("d1:zld1:xi42ee0:e1:a0:e");
        Assert.That(frame.Message["a"], Is.EqualTo(""));
        var list = (IReadOnlyList<object>)frame.Message["z"];
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(((IReadOnlyDictionary<string, object>)list[0])["x"], Is.EqualTo(42L));
        Assert.That(list[1], Is.EqualTo(""));
    }

    [Test]
    public void ReadsAnEmptyDictionary()
        => Assert.That(ReadOne("de").Message, Is.Empty);
}
