using System;
using System.IO;
using System.Text;

namespace EmbeddedNRepl.Hosting;

/// <summary>Forwards bounded chunks to a request output without retaining it after disposal.</summary>
public sealed class ReplOutputTextWriter : TextWriter
{
    private readonly object gate;
    private readonly ReplOutputKind kind;
    private readonly Action<int>? lateOutput;
    private IReplOutput? output;
    private readonly StringBuilder chunk = new StringBuilder(4096);
    private char pending;
    private bool rejected;

    public ReplOutputTextWriter(IReplOutput output, ReplOutputKind kind, object gate)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.kind = kind;
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        lateOutput = (output as IReplEvaluationOutput)?.LateOutput;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (gate)
        {
            if (!Accept(1))
                return;
            Append(value);
            Send();
        }
    }

    public override void Write(string? value)
    {
        if (value == null)
            return;
        lock (gate)
        {
            if (!Accept(value.Length))
                return;
            foreach (var character in value)
            {
                Append(character);
                if (rejected)
                    break;
            }
            Send();
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (index < 0 || count < 0 || index > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        lock (gate)
        {
            if (!Accept(count))
                return;
            for (var i = index; i < index + count && !rejected; i++)
                Append(buffer[i]);
            Send();
        }
    }

    private bool Accept(int count)
    {
        if (output != null)
            return !rejected;
        lateOutput?.Invoke(count);
        return false;
    }

    private void Append(char character)
    {
        if (pending != 0)
        {
            if (char.IsLowSurrogate(character))
            {
                chunk.Append(pending).Append(character);
                pending = '\0';
                if (chunk.Length >= 4094)
                    Send();
                return;
            }
            chunk.Append('\uFFFD');
            pending = '\0';
        }
        if (char.IsHighSurrogate(character))
            pending = character;
        else
            chunk.Append(char.IsLowSurrogate(character) ? '\uFFFD' : character);
        if (chunk.Length >= 4094)
            Send();
    }

    private void Send()
    {
        if (chunk.Length == 0)
            return;
        if (!rejected && output != null)
        {
            var text = chunk.ToString();
            rejected = !output.TryWrite(kind, text, 0, text.Length);
        }
        chunk.Clear();
    }

    public override void Flush()
    {
        lock (gate)
        {
            Send();
            output?.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (gate)
        {
            if (output == null)
                return;
            if (pending != 0)
            {
                chunk.Append('\uFFFD');
                pending = '\0';
            }
            Send();
            output = null;
        }
        base.Dispose(disposing);
    }
}
