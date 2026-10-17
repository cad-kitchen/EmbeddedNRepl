using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EmbeddedNRepl.Protocol;

public static class BencodeEncoder
{
    public static byte[] Encode(IReadOnlyDictionary<string, object> message, int maximumBytes)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using (var writer = new Writer(maximumBytes))
        {
            writer.Value(message, 1);
            return writer.ToArray();
        }
    }

    private sealed class Writer : IDisposable
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly MemoryStream stream = new MemoryStream();
        private readonly int maximumBytes;
        public Writer(int maximumBytes)
        {
            this.maximumBytes = maximumBytes;
        }
        public byte[] ToArray() => stream.ToArray();
        public void Dispose() => stream.Dispose();

        private void Ensure(long bytes)
        {
            if (bytes > maximumBytes - stream.Length)
                throw new BencodeException("Encoded frame exceeds its budget.");
        }
        private void Byte(char value)
        {
            Ensure(1);
            stream.WriteByte((byte)value);
        }
        private void Bytes(byte[] value)
        {
            Ensure(value.Length);
            stream.Write(value, 0, value.Length);
        }
        private void Ascii(string value) => Bytes(Encoding.ASCII.GetBytes(value));
        private byte[] TextBytes(string text)
        {
            try
            {
                Ensure(Utf8.GetByteCount(text));
                return Utf8.GetBytes(text);
            }
            catch (EncoderFallbackException) { throw new BencodeException("Text contains an invalid UTF-16 sequence."); }
        }
        private void Text(byte[] bytes)
        {
            Ascii(bytes.Length.ToString(CultureInfo.InvariantCulture));
            Byte(':');
            Bytes(bytes);
        }

        public void Value(object value, int depth)
        {
            if (depth > 32)
                throw new BencodeException("Outgoing container depth exceeded.");
            if (value is string text)
            {
                Text(TextBytes(text));
                return;
            }
            if (value is long number)
            {
                Byte('i');
                Ascii(number.ToString(CultureInfo.InvariantCulture));
                Byte('e');
                return;
            }
            if (value is IReadOnlyDictionary<string, object> dictionary)
            {
                Byte('d');
                var entries = new List<KeyValuePair<byte[], object>>();
                long keyBytes = 0;
                foreach (var entry in dictionary)
                {
                    byte[] key = TextBytes(entry.Key);
                    keyBytes += key.Length + 2L;
                    Ensure(keyBytes);
                    entries.Add(new KeyValuePair<byte[], object>(key, entry.Value));
                }
                entries.Sort((left, right) => CompareBytes(left.Key, right.Key));
                foreach (var entry in entries)
                {
                    Text(entry.Key);
                    Value(entry.Value, depth + 1);
                }
                Byte('e');
                return;
            }
            if (value is IEnumerable<object> list)
            {
                Byte('l');
                foreach (object item in list)
                    Value(item, depth + 1);
                Byte('e');
                return;
            }
            throw new BencodeException("Only text, Int64, lists, and dictionaries can be encoded.");
        }

        private static int CompareBytes(byte[] left, byte[] right)
        {
            int count = Math.Min(left.Length, right.Length);
            for (int i = 0; i < count; i++)
            {
                int comparison = left[i].CompareTo(right[i]);
                if (comparison != 0)
                    return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
