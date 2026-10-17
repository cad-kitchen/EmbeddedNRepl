using System;
using System.IO;
using System.Text;

namespace EmbeddedNRepl.Hosting;

/// <summary>A bounded UTF-8 sink for language printers.</summary>
public sealed class Utf8QuotaWriter : TextWriter
{
    private readonly int quota;
    private readonly StringBuilder text;
    private int bytes;
    private char pending;

    public Utf8QuotaWriter(int quota)
    {
        if (quota <= 0)
            throw new ArgumentOutOfRangeException(nameof(quota));
        this.quota = quota;
        text = new StringBuilder(Math.Min(quota, 4096));
    }

    public bool Exceeded
    {
        get; private set;
    }
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(string? value)
    {
        if (value != null)
            foreach (var character in value)
                Write(character);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (index < 0 || count < 0 || index > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        for (var i = index; i < index + count; i++)
            Write(buffer[i]);
    }

    public override void Write(char character)
    {
        if (Exceeded)
            throw new Utf8QuotaExceededException();
        if (pending != 0)
        {
            if (char.IsLowSurrogate(character))
            {
                Reserve(4);
                text.Append(pending).Append(character);
                pending = '\0';
                return;
            }
            Reserve(3);
            text.Append('\uFFFD');
            pending = '\0';
        }
        if (char.IsHighSurrogate(character))
        {
            pending = character;
            return;
        }
        Reserve(character < 128 ? 1 : character < 2048 ? 2 : 3);
        text.Append(char.IsLowSurrogate(character) ? '\uFFFD' : character);
    }

    private void Reserve(int size)
    {
        if (size > quota - bytes)
        {
            Exceeded = true;
            throw new Utf8QuotaExceededException();
        }
        bytes += size;
    }

    public void Finish()
    {
        if (pending == 0)
            return;
        Reserve(3);
        text.Append('\uFFFD');
        pending = '\0';
    }

    public override string ToString() => text.ToString();
}

public sealed class Utf8QuotaExceededException : Exception
{
}
