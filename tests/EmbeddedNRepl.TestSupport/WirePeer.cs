using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbeddedNRepl.Testing;

/// <summary>Small independent bencode oracle. It deliberately does not use the production codec.</summary>
public sealed class WirePeer : IDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private WirePeer(TcpClient client)
    {
        this.client = client;
        stream = client.GetStream();
    }
    public static Task<WirePeer> Connect(ReplEndpoint endpoint) => Connect(endpoint.Address, endpoint.Port);
    public static async Task<WirePeer> Connect(IPAddress address, int port)
    {
        var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        try
        {
            await TestDeadline.Complete(client.ConnectAsync(address, port));
            return new WirePeer(client);
        }
        catch { client.Dispose(); throw; }
    }
    public static Dictionary<string, object> Message(params object[] pairs)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
            result.Add((string)pairs[i], pairs[i + 1]);
        return result;
    }
    public async Task Send(params Dictionary<string, object>[] messages)
    {
        using (var buffer = new MemoryStream())
        {
            foreach (var message in messages)
                Write(buffer, message);
            await SendBytes(buffer.ToArray());
        }
    }
    public Task SendBytes(byte[] bytes) => TestDeadline.Complete(stream.WriteAsync(bytes, 0, bytes.Length));
    public async Task<Dictionary<string, object>?> ReceiveOrEnd()
    {
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (deadline.Token.Register(() => client.Dispose()))
        {
            try
            {
                var first = new byte[1];
                if (await stream.ReadAsync(first, 0, 1) == 0)
                    return null;
                return (Dictionary<string, object>)await Read(stream, first[0], 0);
            }
            catch (Exception error) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException("No complete server response within the wire fixture deadline.", error);
            }
        }
    }
    public async Task<Dictionary<string, object>> Receive() =>
        await ReceiveOrEnd() ?? throw new EndOfStreamException("The server closed before responding.");
    public static byte[] Encode(Dictionary<string, object> message)
    {
        using (var buffer = new MemoryStream())
        {
            Write(buffer, message);
            return buffer.ToArray();
        }
    }
    public static async Task<Dictionary<string, object>> Decode(byte[] bytes)
    {
        using (var buffer = new MemoryStream(bytes))
        {
            var value = (Dictionary<string, object>)await Read(buffer, await Next(buffer), 0);
            if (buffer.Position != buffer.Length)
                throw new InvalidDataException("Trailing fixture bytes.");
            return value;
        }
    }
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static void Write(Stream target, object value)
    {
        if (value is string text)
        {
            var bytes = Utf8.GetBytes(text);
            Ascii(target, bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
            target.Write(bytes, 0, bytes.Length);
        }
        else if (value is long number)
            Ascii(target, "i" + number.ToString(CultureInfo.InvariantCulture) + "e");
        else if (value is Dictionary<string, object> map)
        {
            target.WriteByte((byte)'d');
            // Incoming dictionaries are allowed to be unsorted.
            foreach (var entry in map)
            {
                Write(target, entry.Key);
                Write(target, entry.Value);
            }
            target.WriteByte((byte)'e');
        }
        else if (value is IEnumerable<object> list)
        {
            target.WriteByte((byte)'l');
            foreach (var item in list)
                Write(target, item);
            target.WriteByte((byte)'e');
        }
        else
            throw new InvalidDataException("Unsupported fixture value.");
    }
    private static void Ascii(Stream target, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        target.Write(bytes, 0, bytes.Length);
    }
    private static async Task<byte> Next(Stream source)
    {
        var one = new byte[1];
        if (await source.ReadAsync(one, 0, 1) == 0)
            throw new EndOfStreamException();
        return one[0];
    }
    private static async Task<object> Read(Stream source, byte token, int depth)
    {
        if (depth > 32)
            throw new InvalidDataException("Fixture nesting exceeded.");
        if (token == 'd')
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var next = await Next(source); next != 'e'; next = await Next(source))
            {
                if (map.Count >= 16384)
                    throw new InvalidDataException();
                var key = (string)await Read(source, next, depth + 1);
                map.Add(key, await Read(source, await Next(source), depth + 1));
            }
            return map;
        }
        if (token == 'l')
        {
            var list = new List<object>();
            for (var next = await Next(source); next != 'e'; next = await Next(source))
            {
                if (list.Count >= 16384)
                    throw new InvalidDataException();
                list.Add(await Read(source, next, depth + 1));
            }
            return list;
        }
        var integer = token == 'i';
        var text = integer ? "" : ((char)token).ToString();
        for (var next = await Next(source); next != (integer ? 'e' : ':'); next = await Next(source))
        {
            if (text.Length >= 32)
                throw new InvalidDataException();
            text += (char)next;
        }
        if (integer)
            return long.Parse(text, CultureInfo.InvariantCulture);
        var length = int.Parse(text, CultureInfo.InvariantCulture);
        if (length < 0 || length > 1048576)
            throw new InvalidDataException();
        var bytes = new byte[length];
        for (var offset = 0; offset < length;)
        {
            var count = await source.ReadAsync(bytes, offset, length - offset);
            if (count == 0)
                throw new EndOfStreamException();
            offset += count;
        }
        return Utf8.GetString(bytes);
    }
    public void Dispose() => client.Dispose();
}
