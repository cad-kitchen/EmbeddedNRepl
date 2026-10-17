using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using EmbeddedNRepl.Testing;

namespace EmbeddedNRepl.TransportTests;

internal sealed class LoopbackPair : IDisposable
{
    private LoopbackPair(TcpClient client, TcpClient server)
    {
        Client = client;
        Server = server;
    }
    public TcpClient Client
    {
        get;
    }
    public TcpClient Server
    {
        get;
    }
    public static async Task<LoopbackPair> Create()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var client = new TcpClient { NoDelay = true };
        try
        {
            listener.Start();
            var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await TestDeadline.Complete(listener.AcceptTcpClientAsync());
            await TestDeadline.Complete(connect);
            server.NoDelay = true;
            return new LoopbackPair(client, server);
        }
        catch { client.Dispose(); throw; }
        finally { listener.Stop(); }
    }
    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
    }
}
