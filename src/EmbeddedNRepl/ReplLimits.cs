using System;

namespace EmbeddedNRepl;

/// <summary>Basic bounds for an unauthenticated loopback developer endpoint.</summary>
public sealed class ReplLimits
{
    public int MessageBytes { get; set; } = 1048576;
    public int StringBytes { get; set; } = 1048576;
    public int ContainerDepth { get; set; } = 32;
    public int DecodedValues { get; set; } = 16384;
    public int NumericTokenBytes { get; set; } = 32;
    public int RequestIdBytes { get; set; } = 256;
    public int Connections { get; set; } = 16;
    public int PersistentSessions { get; set; } = 64;
    public int OutputBytes { get; set; } = 1048576;
    public int ValueBytes { get; set; } = 65536;
    public TimeSpan PartialFrameTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal ReplLimits Snapshot()
    {
        Validate();
        return (ReplLimits)MemberwiseClone();
    }
    public void Validate()
    {
        Positive(MessageBytes, nameof(MessageBytes));
        Positive(StringBytes, nameof(StringBytes));
        Positive(ContainerDepth, nameof(ContainerDepth));
        Positive(DecodedValues, nameof(DecodedValues));
        Positive(NumericTokenBytes, nameof(NumericTokenBytes));
        Positive(RequestIdBytes, nameof(RequestIdBytes));
        Positive(Connections, nameof(Connections));
        Positive(PersistentSessions, nameof(PersistentSessions));
        Positive(OutputBytes, nameof(OutputBytes));
        Positive(ValueBytes, nameof(ValueBytes));
        if (StringBytes > MessageBytes)
            throw new ArgumentException("StringBytes exceeds MessageBytes.", nameof(StringBytes));
        if (RequestIdBytes > StringBytes)
            throw new ArgumentException("RequestIdBytes exceeds StringBytes.", nameof(RequestIdBytes));
        if (PartialFrameTimeout <= TimeSpan.Zero || PartialFrameTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PartialFrameTimeout));
    }
    private static void Positive(long value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name);
    }
}
