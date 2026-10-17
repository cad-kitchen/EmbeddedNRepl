using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace EmbeddedNRepl.Protocol;

/// <summary>Incremental single-reader decoder. A protocol failure is terminal for this instance.</summary>
public sealed class BencodeDecoder
{
    private enum Mode
    {
        Value, Length, String, Integer
    }
    private sealed class Container
    {
        public Container(bool dictionary)
        {
            if (dictionary)
                Dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
            else
                List = new List<object>();
        }
        public Dictionary<string, object>? Dictionary
        {
            get;
        }
        public List<object>? List
        {
            get;
        }
        public string? Key
        {
            get; set;
        }
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly Stack<Container> containers = new Stack<Container>();
    private readonly int maximumStringBytes;
    private readonly int maximumMessageBytes;
    private readonly int maximumDepth;
    private readonly int maximumValues;
    private readonly int maximumNumericBytes;
    private Mode mode;
    private int frameBytes;
    private long number;
    private bool negative;
    private int digits;
    private byte[] body = Array.Empty<byte>();
    private int bodyPosition;
    private BencodeFrame? completed;
    private int values;
    private bool failed;

    public BencodeDecoder(ReplLimits limits)
    {
        if (limits == null)
            throw new ArgumentNullException(nameof(limits));
        limits.Validate();
        maximumStringBytes = limits.StringBytes;
        maximumMessageBytes = limits.MessageBytes;
        maximumDepth = limits.ContainerDepth;
        maximumValues = limits.DecodedValues;
        maximumNumericBytes = limits.NumericTokenBytes;
    }
    public bool HasPartialFrame => frameBytes != 0;

    public BencodeFrame? Read(byte[] bytes, int offset, int count, out int consumed)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        if (offset < 0 || count < 0 || offset > bytes.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        consumed = 0;
        if (failed)
            throw new BencodeException("The decoder has already failed.");
        try
        {
            return ReadCore(bytes, offset, count, out consumed);
        }
        catch (BencodeException)
        {
            failed = true;
            containers.Clear();
            body = Array.Empty<byte>();
            throw;
        }
    }

    private BencodeFrame? ReadCore(byte[] bytes, int offset, int count, out int consumed)
    {
        consumed = 0;
        while (consumed < count)
        {
            byte next = bytes[offset + consumed++];
            if (frameBytes >= maximumMessageBytes)
                throw new BencodeException("Message limit exceeded.");
            frameBytes++;
            switch (mode)
            {
                case Mode.Value:
                    StartValue(next);
                    break;
                case Mode.Length:
                    ReadLength(next);
                    break;
                case Mode.String:
                    body[bodyPosition++] = next;
                    if (bodyPosition == body.Length)
                        FinishString();
                    break;
                case Mode.Integer:
                    ReadInteger(next);
                    break;
            }
            if (completed != null)
            {
                var result = completed;
                completed = null;
                frameBytes = 0;
                values = 0;
                return result;
            }
        }
        return null;
    }

    private void StartValue(byte next)
    {
        if (frameBytes == 1 && next != (byte)'d')
            throw new BencodeException("A message must be a dictionary.");
        if (next == (byte)'e')
        {
            if (containers.Count == 0)
                throw new BencodeException("Unexpected container terminator.");
            Container top = containers.Pop();
            if (top.Key != null)
                throw new BencodeException("A dictionary key has no value.");
            object value = top.Dictionary != null
                ? (object)new ReadOnlyDictionary<string, object>(top.Dictionary)
                : top.List!.AsReadOnly();
            if (containers.Count == 0)
                completed = new BencodeFrame((IReadOnlyDictionary<string, object>)value, frameBytes);
            else
                AddValue(value);
            return;
        }
        if (values >= maximumValues)
            throw new BencodeException("Decoded-value limit exceeded.");
        values++;
        if (next >= (byte)'0' && next <= (byte)'9')
        {
            mode = Mode.Length;
            number = next - (byte)'0';
            digits = 1;
            return;
        }
        if (containers.Count > 0 && containers.Peek().Dictionary != null && containers.Peek().Key == null)
            throw new BencodeException("Dictionary keys must be strings.");
        if (next == (byte)'d' || next == (byte)'l')
        {
            if (containers.Count >= maximumDepth)
                throw new BencodeException("Container depth exceeded.");
            containers.Push(new Container(next == (byte)'d'));
            return;
        }
        if (next == (byte)'i')
        {
            mode = Mode.Integer;
            number = 0;
            digits = 0;
            negative = false;
            return;
        }
        throw new BencodeException("Invalid value token.");
    }

    private void ReadLength(byte next)
    {
        if (next == (byte)':')
        {
            if (number > maximumStringBytes)
                throw new BencodeException("String limit exceeded.");
            if (number > maximumMessageBytes - frameBytes - containers.Count)
                throw new BencodeException("Declared string cannot fit in the remaining message.");
            body = new byte[(int)number];
            bodyPosition = 0;
            mode = Mode.String;
            if (body.Length == 0)
                FinishString();
            return;
        }
        if (next < (byte)'0' || next > (byte)'9')
            throw new BencodeException("Invalid string length.");
        if (number == 0)
            throw new BencodeException("Leading zero in string length.");
        if (digits >= maximumNumericBytes)
            throw new BencodeException("Numeric-token limit exceeded.");
        try
        {
            number = checked(number * 10 + next - (byte)'0');
        }
        catch (OverflowException) { throw new BencodeException("String length overflow."); }
        digits++;
    }

    private void FinishString()
    {
        string text;
        try
        {
            text = Utf8.GetString(body, 0, body.Length);
        }
        catch (DecoderFallbackException) { throw new BencodeException("Invalid UTF-8 text."); }
        body = Array.Empty<byte>();
        mode = Mode.Value;
        AddValue(text);
    }

    private void ReadInteger(byte next)
    {
        if (next == (byte)'-' && digits == 0 && !negative)
        {
            negative = true;
            return;
        }
        if (next == (byte)'e')
        {
            if (digits == 0)
                throw new BencodeException("Empty integer.");
            if (negative && number == 0)
                throw new BencodeException("Negative zero.");
            long value;
            try
            {
                value = negative ? number : checked(-number);
            }
            catch (OverflowException) { throw new BencodeException("Integer overflow."); }
            mode = Mode.Value;
            AddValue(value);
            return;
        }
        if (next < (byte)'0' || next > (byte)'9')
            throw new BencodeException("Invalid integer.");
        if (digits > 0 && number == 0)
            throw new BencodeException("Leading zero in integer.");
        if (digits + (negative ? 1 : 0) >= maximumNumericBytes)
            throw new BencodeException("Numeric-token limit exceeded.");
        try
        {
            number = checked(number * 10 - (next - (byte)'0'));
        }
        catch (OverflowException) { throw new BencodeException("Integer overflow."); }
        digits++;
    }

    private void AddValue(object value)
    {
        Container top = containers.Peek();
        if (top.Dictionary == null)
            top.List!.Add(value);
        else if (top.Key == null)
        {
            string key = (string)value;
            if (top.Dictionary.ContainsKey(key))
                throw new BencodeException("Duplicate dictionary key.");
            top.Key = key;
        }
        else
        {
            top.Dictionary[top.Key] = value;
            top.Key = null;
        }
    }
    public void Complete()
    {
        if (failed || HasPartialFrame)
        {
            failed = true;
            containers.Clear();
            body = Array.Empty<byte>();
            throw new BencodeException("End of stream during an invalid or incomplete message.");
        }
    }
}
