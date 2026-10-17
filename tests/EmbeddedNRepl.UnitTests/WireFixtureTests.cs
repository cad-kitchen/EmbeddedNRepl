using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using EmbeddedNRepl.Testing;
using NUnit.Framework;

namespace EmbeddedNRepl.UnitTests;

public sealed class WireFixtureTests
{
    [Test]
    public async Task IndependentFixtureReadsNestedUnicodeAndPreservesFrames()
    {
        const string vector = "d2:id6:я😀3:opsd8:describedee6:statusl4:donee1:ni-9223372036854775808ee";
        var response = await WirePeer.Decode(Encoding.UTF8.GetBytes(vector));
        Assert.That(response["id"], Is.EqualTo("я😀"));
        Assert.That((Dictionary<string, object>)response["ops"], Contains.Key("describe"));
        Assert.That(response["status"], Is.EqualTo(new[] { "done" }));
        Assert.That(response["n"], Is.EqualTo(long.MinValue));
        Assert.That(Encoding.UTF8.GetString(WirePeer.Encode(WirePeer.Message("op", "eval", "code", "я😀"))),
            Is.EqualTo("d2:op4:eval4:code6:я😀e"));
    }
}
