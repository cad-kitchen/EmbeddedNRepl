using System.IO;

namespace EmbeddedNRepl.Protocol;

public sealed class BencodeException : IOException
{
    public BencodeException(string message) : base(message) { }
}
