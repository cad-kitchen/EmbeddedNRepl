using System;
using System.Net;
using EmbeddedNRepl.Hosting;

namespace EmbeddedNRepl;

public sealed class ReplEndpoint
{
    public ReplEndpoint(IPAddress address, int port, Guid serverInstanceId, ReplHostIdentity host)
    {
        Address = address;
        Port = port;
        ServerInstanceId = serverInstanceId;
        Host = host;
    }
    public IPAddress Address
    {
        get;
    }
    public int Port
    {
        get;
    }
    public Guid ServerInstanceId
    {
        get;
    }
    public ReplHostIdentity Host
    {
        get;
    }
    public string Profile => "embedded-nrepl-clojureclr-v1";
    public string AccessProfile => "trusted-loopback";
    public string AccessDescription => "Unauthenticated developer endpoint: local processes, including processes of other users, may execute code.";
}

public sealed class ReplStartupException : InvalidOperationException
{
    public ReplStartupException(string code, string message, Exception? inner = null) : base(message, inner) { Code = code; }
    public string Code
    {
        get;
    }
}
