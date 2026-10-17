using System.Collections.Generic;

namespace EmbeddedNRepl.Protocol;

public sealed class BencodeFrame
{
    public BencodeFrame(IReadOnlyDictionary<string, object> message, int sizeBytes)
    {
        Message = message;
        SizeBytes = sizeBytes;
    }
    public IReadOnlyDictionary<string, object> Message
    {
        get;
    }
    public int SizeBytes
    {
        get;
    }
}
