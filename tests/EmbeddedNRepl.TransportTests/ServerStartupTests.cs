using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using EmbeddedNRepl.Testing;
using NUnit.Framework;

namespace EmbeddedNRepl.TransportTests;

public sealed class ServerStartupTests
{
    [Test]
    public async Task OccupiedPortFailsWithoutFallbackAndCanBeRetriedExplicitly()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        occupied.Start();
        var requested = (IPEndPoint)occupied.LocalEndpoint;
        var server = new ReplServer(new ServerFixture().Services);
        try
        {
            var error = Assert.Throws<ReplStartupException>(() => server.Start(true, requested));
            Assert.That(error!.Code, Is.EqualTo("embedded-nrepl/listener-start-failed"));
            Assert.That(error.InnerException, Is.TypeOf<SocketException>());
            Assert.That(server.Endpoint, Is.Null);
            occupied.Stop();
            Assert.That(server.Start(true, requested)!.Port, Is.EqualTo(requested.Port));
        }
        finally { occupied.Stop(); await server.StopAsync(); }
    }

    [Test]
    public async Task ActivationDoesNotModifyAnotherComponentsDiscoveryFiles()
    {
        string originalDirectory = Directory.GetCurrentDirectory();
        string directory = Path.Combine(Path.GetTempPath(), "embedded-nrepl-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string portFile = Path.Combine(directory, ".nrepl-port");
        string discoveryFile = Path.Combine(directory, "discovery.json");
        File.WriteAllText(portFile, "7888");
        File.WriteAllText(discoveryFile, "{\"owner\":\"another-component\"}");
        var written = File.GetLastWriteTimeUtc(portFile);
        var server = new ReplServer(new ServerFixture().Services);
        try
        {
            Directory.SetCurrentDirectory(directory);
            server.Start(true, new IPEndPoint(IPAddress.Loopback, 0));
            await server.StopAsync();
            Assert.That(File.ReadAllText(portFile), Is.EqualTo("7888"));
            Assert.That(File.GetLastWriteTimeUtc(portFile), Is.EqualTo(written));
            Assert.That(File.ReadAllText(discoveryFile), Is.EqualTo("{\"owner\":\"another-component\"}"));
        }
        finally
        {
            await server.StopAsync();
            Directory.SetCurrentDirectory(originalDirectory);
            File.Delete(portFile);
            File.Delete(discoveryFile);
            Directory.Delete(directory);
        }
    }

    [Test]
    public void InvalidResourceLimitsAreRejectedBeforeActivation()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReplServer(new ServerFixture().Services, new ReplLimits { MessageBytes = 0 }));

    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    public async Task ExplicitActivationReportsTheBoundEndpointAndReleasesThePort(string address)
    {
        var owner = new ServerFixture();
        var server = new ReplServer(owner.Services);
        var requested = new IPEndPoint(IPAddress.Parse(address), 0);
        ReplEndpoint? endpoint = null;
        try
        {
            endpoint = server.Start(true, requested);
            Assert.That(endpoint, Is.Not.Null);
            Assert.That(endpoint!.Address, Is.EqualTo(requested.Address));
            Assert.That(endpoint.Port, Is.GreaterThan(0));
            Assert.That(endpoint.ServerInstanceId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(endpoint.Host, Is.SameAs(owner.Services.Identity));
            Assert.That(endpoint.Profile, Is.EqualTo("embedded-nrepl-clojureclr-v1"));
            Assert.That(endpoint.AccessProfile, Is.EqualTo("trusted-loopback"));
            Assert.That(endpoint.AccessDescription, Does.Contain("other users"));
            Assert.That(server.Start(true, requested), Is.SameAs(endpoint));
            using var client = new TcpClient(requested.AddressFamily);
            await TestDeadline.Complete(client.ConnectAsync(endpoint.Address, endpoint.Port));
            Assert.That(owner.Evaluator.Calls, Is.Empty);
            Assert.That(owner.States.Active, Is.Zero);
        }
        finally { await server.StopAsync(); await server.StopAsync(); }
        Assert.That(server.Endpoint, Is.Null);
        var probe = new TcpListener(endpoint!.Address, endpoint.Port);
        try
        {
            probe.Start();
        }
        finally { probe.Stop(); }
        try
        {
            var restarted = server.Start(true, requested);
            Assert.That(restarted!.ServerInstanceId, Is.Not.EqualTo(endpoint.ServerInstanceId));
        }
        finally { await server.StopAsync(); }
    }

    [TestCase("0.0.0.0")]
    [TestCase("::")]
    [TestCase("192.0.2.1")]
    public async Task NonLoopbackIsRejectedBeforeBinding(string address)
    {
        var server = new ReplServer(new ServerFixture().Services);
        try
        {
            var error = Assert.Throws<ReplStartupException>(() => server.Start(true, new IPEndPoint(IPAddress.Parse(address), 0)));
            Assert.That(error!.Code, Is.EqualTo("embedded-nrepl/non-loopback"));
            Assert.That(server.Endpoint, Is.Null);
        }
        finally { await server.StopAsync(); }
    }

    [Test]
    public async Task NormalProfileDoesNotBindOrInitializeTheRuntime()
    {
        var owner = new ServerFixture();
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var server = new ReplServer(owner.Services);
        try
        {
            Assert.That(server.Start(false, (IPEndPoint)occupied.LocalEndpoint), Is.Null);
            Assert.That(server.Endpoint, Is.Null);
            Assert.That(owner.Evaluator.Calls, Is.Empty);
            Assert.That(owner.States.Active, Is.Zero);
            Assert.That(owner.Scheduler.Queued, Is.Zero);
        }
        finally { await server.StopAsync(); occupied.Stop(); }
    }
}
