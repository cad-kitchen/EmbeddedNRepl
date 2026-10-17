using System;
using System.Collections.Generic;
using System.Text;
using EmbeddedNRepl.Protocol;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class BencodeEncoderTests
{
    [Test]
    public void MatchesAnIndependentCanonicalWireVector()
    {
        var message = new Dictionary<string, object>
        {
            ["z"] = new object[] { long.MinValue, "", new Dictionary<string, object> { ["x"] = 42L } },
            ["a"] = "Ж中🙂"
        };
        byte[] expected = Encoding.UTF8.GetBytes("d1:a9:Ж中🙂1:zli-9223372036854775808e0:d1:xi42eeee");
        Assert.That(BencodeEncoder.Encode(message, 1024), Is.EqualTo(expected));
    }

    [Test]
    public void KeyOrderUsesUtf8BytesIncludingSupplementaryCharacters()
    {
        var message = new Dictionary<string, object> { ["🙂"] = 1L, ["\ue000"] = 2L, ["a"] = 3L };
        Assert.That(BencodeEncoder.Encode(message, 1024),
            Is.EqualTo(Encoding.UTF8.GetBytes("d1:ai3e3:\ue000i2e4:🙂i1ee")));
    }

    [Test]
    public void ExactFrameBudgetIsAcceptedAndOneByteLessRejected()
    {
        var message = new Dictionary<string, object> { ["n"] = long.MaxValue };
        byte[] expected = Encoding.ASCII.GetBytes("d1:ni9223372036854775807ee");
        Assert.That(BencodeEncoder.Encode(message, expected.Length), Is.EqualTo(expected));
        Assert.Throws<BencodeException>(() => BencodeEncoder.Encode(message, expected.Length - 1));
    }

    [Test]
    public void InvalidUtf16AndUnsupportedValuesAreNotSilentlyCoerced()
    {
        Assert.Throws<BencodeException>(() => BencodeEncoder.Encode(new Dictionary<string, object> { ["x"] = "\ud800" }, 1024));
        Assert.Throws<BencodeException>(() => BencodeEncoder.Encode(new Dictionary<string, object> { ["x"] = 1.5 }, 1024));
    }

    [Test]
    public void CyclicValuesFailWithinTheFrameBudget()
    {
        var message = new Dictionary<string, object>();
        message["self"] = message;
        Assert.Throws<BencodeException>(() => BencodeEncoder.Encode(message, 1024));
    }
}
