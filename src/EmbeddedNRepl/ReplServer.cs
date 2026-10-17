using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedNRepl.Hosting;
using EmbeddedNRepl.Protocol;

namespace EmbeddedNRepl;

/// <summary>A small loopback nREPL server. The host owns runtime scheduling.</summary>
public sealed class ReplServer
{
    private readonly object gate = new object();
    private readonly ReplServices services;
    private readonly ReplLimits limits;
    private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
    private readonly HashSet<Connection> connections = new HashSet<Connection>();
    private TcpListener? listener;
    private Task accepting = Task.CompletedTask;
    private ReplEndpoint? endpoint;

    public ReplServer(ReplServices services, ReplLimits? limits = null)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.limits = (limits ?? new ReplLimits()).Snapshot();
    }

    public ReplEndpoint? Endpoint
    {
        get
        {
            lock (gate)
                return endpoint;
        }
    }

    public ReplEndpoint? Start(bool developerMode, IPEndPoint address)
    {
        if (!developerMode)
            return null;
        if (address == null)
            throw new ArgumentNullException(nameof(address));
        if (!IPAddress.IsLoopback(address.Address))
            throw new ReplStartupException("embedded-nrepl/non-loopback", "Developer nREPL accepts only a loopback address.");
        lock (gate)
        {
            if (endpoint != null)
                return endpoint;
            var candidate = new TcpListener(address) { ExclusiveAddressUse = true };
            try
            {
                candidate.Start();
            }
            catch (SocketException error)
            {
                candidate.Stop();
                throw new ReplStartupException("embedded-nrepl/listener-start-failed", "Unable to bind the requested nREPL endpoint.", error);
            }
            listener = candidate;
            var bound = (IPEndPoint)candidate.LocalEndpoint;
            endpoint = new ReplEndpoint(bound.Address, bound.Port, Guid.NewGuid(), services.Identity);
            accepting = Task.Run(() => AcceptAsync(candidate));
            return endpoint;
        }
    }

    public async Task<ReplStopResult> StopAsync()
    {
        TcpListener? stopped;
        Connection[] open;
        Session[] retained;
        Task accept;
        lock (gate)
        {
            stopped = listener;
            listener = null;
            endpoint = null;
            accept = accepting;
            open = connections.ToArray();
            retained = sessions.Values.ToArray();
            sessions.Clear();
            foreach (var session in retained)
                session.Closing = true;
        }
        stopped?.Stop();
        foreach (var connection in open)
            connection.Close();
        try
        {
            await accept.ConfigureAwait(false);
        }
        catch (Exception) when (stopped != null) { }
        foreach (var session in retained)
            TryRelease(session);
        return new ReplStopResult(true);
    }

    private async Task AcceptAsync(TcpListener owner)
    {
        try
        {
            while (true)
            {
                var client = await owner.AcceptTcpClientAsync().ConfigureAwait(false);
                client.NoDelay = true;
                var connection = new Connection(client);
                lock (gate)
                {
                    if (!ReferenceEquals(listener, owner) || connections.Count >= limits.Connections)
                    {
                        connection.Close();
                        continue;
                    }
                    connections.Add(connection);
                }
                _ = Task.Run(() => RunConnectionAsync(connection));
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (InvalidOperationException) { }
    }

    private async Task RunConnectionAsync(Connection connection)
    {
        try
        {
            using (connection.Client)
            using (var stream = connection.Client.GetStream())
            using (var reader = new BencodeStreamReader(stream, limits))
            {
                connection.Stream = stream;
                BencodeFrame? frame;
                while ((frame = await reader.ReadAsync().ConfigureAwait(false)) != null)
                    await HandleAsync(connection, frame.Message).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is BencodeException || error is IOException || error is SocketException ||
            error is ObjectDisposedException || error is TimeoutException)
        {
        }
        finally
        {
            connection.Close();
            lock (gate)
                connections.Remove(connection);
        }
    }

    private async Task HandleAsync(Connection connection, IReadOnlyDictionary<string, object> message)
    {
        ReplRequest request;
        try
        {
            request = ReplRequest.Parse(message, limits);
        }
        catch (ReplRequestException error)
        {
            var response = Response(message, new[] { "error", error.Code, "done" });
            response["ex"] = typeof(ReplRequestException).FullName!;
            response["err"] = ReplEvaluationError.Limit(error.Message, 1024) + "\n";
            await SendAsync(connection, response).ConfigureAwait(false);
            return;
        }
        switch (request.Op)
        {
            case "describe":
                await SendAsync(connection, Describe(request)).ConfigureAwait(false);
                break;
            case "clone":
                await CloneAsync(connection, request).ConfigureAwait(false);
                break;
            case "close":
                await CloseAsync(connection, request).ConfigureAwait(false);
                break;
            case "ls-sessions":
                await SessionsAsync(connection, request).ConfigureAwait(false);
                break;
            case "eval":
                await EvalAsync(connection, request).ConfigureAwait(false);
                break;
            default:
                await SendAsync(connection, Response(request, new[] { "unknown-op", "done" })).ConfigureAwait(false);
                break;
        }
    }

    private async Task CloneAsync(Connection connection, ReplRequest request)
    {
        Session? created = null;
        try
        {
            lock (gate)
            {
                if (sessions.Count < limits.PersistentSessions)
                {
                    IReplSessionState? state = request.Session == null ? services.Sessions.Create() :
                        sessions.TryGetValue(request.Session, out var parent) ? services.Sessions.Clone(parent.Published) : null;
                    if (state != null)
                    {
                        created = new Session(state, services.Sessions.Snapshot(state));
                        sessions.Add(created.Id, created);
                    }
                }
            }
        }
        catch (Exception)
        {
            await SendAsync(connection, Response(request, new[] { "error", "session-error", "done" })).ConfigureAwait(false);
            return;
        }
        if (created == null)
        {
            var code = request.Session != null ? "unknown-session" : "session-limit";
            await SendAsync(connection, Response(request, new[] { "error", code, "done" })).ConfigureAwait(false);
            return;
        }
        var response = Response(request, new[] { "done" });
        response["new-session"] = created.Id;
        await SendAsync(connection, response).ConfigureAwait(false);
    }

    private async Task CloseAsync(Connection connection, ReplRequest request)
    {
        Session? session = null;
        lock (gate)
        {
            if (request.Session != null && sessions.TryGetValue(request.Session, out session))
            {
                sessions.Remove(request.Session);
                session.Closing = true;
            }
        }
        if (session == null)
        {
            await SendAsync(connection, Response(request, new[] { "error", "unknown-session", "done" })).ConfigureAwait(false);
            return;
        }
        TryRelease(session);
        await SendAsync(connection, Response(request, new[] { "session-closed", "done" })).ConfigureAwait(false);
    }

    private Task SessionsAsync(Connection connection, ReplRequest request)
    {
        string[] ids;
        lock (gate)
            ids = sessions.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var response = Response(request, new[] { "done" });
        response["sessions"] = ids;
        return SendAsync(connection, response);
    }

    private async Task EvalAsync(Connection connection, ReplRequest request)
    {
        Session? session = null;
        IReplSessionState? state;
        lock (gate)
        {
            if (request.Session != null && (!sessions.TryGetValue(request.Session, out session) || session.Closing))
                state = null;
            else
            {
                state = session?.State ?? services.Sessions.Create();
                if (session != null)
                    session.Pending++;
            }
        }
        if (state == null)
        {
            await SendAsync(connection, Response(request, new[] { "error", "unknown-session", "done" })).ConfigureAwait(false);
            return;
        }

        var output = new CollectedOutput(request, limits.OutputBytes);
        ReplEvaluationOutcome outcome = ReplEvaluationOutcome.Failed;
        string? failure = null;
        IReplSessionSnapshot? snapshot = null;
        try
        {
            var context = services.Host.Capture();
            var operation = services.Scheduler.Schedule(Guid.NewGuid(), context, async token =>
            {
                if (!services.Host.IsCurrent(context))
                {
                    failure = "embedded-nrepl/stale-context";
                    return;
                }
                outcome = await services.Evaluator.EvaluateAsync(request.Evaluation!, state, context, output, token).ConfigureAwait(false);
                if (session != null)
                    snapshot = services.Sessions.Snapshot(state);
            });
            if (await operation.Completion.ConfigureAwait(false) == ReplScheduleOutcome.CancelledBeforeStart)
                outcome = ReplEvaluationOutcome.Interrupted;
        }
        catch (Exception) { failure = "eval-error"; }
        finally
        {
            if (session == null)
                services.Sessions.Release(state);
            else
            {
                lock (gate)
                {
                    if (snapshot != null)
                        session.Published = snapshot;
                    session.Pending--;
                }
                TryRelease(session);
            }
        }

        foreach (var response in output.Responses)
            if (!await SendAsync(connection, response).ConfigureAwait(false))
                return;
        var status = new List<string>();
        if (failure != null)
        {
            status.Add("error");
            status.Add(failure);
        }
        else if (outcome == ReplEvaluationOutcome.Failed)
        {
            status.Add("error");
            status.Add(output.ErrorCode ?? "eval-error");
        }
        else if (outcome == ReplEvaluationOutcome.Interrupted)
            status.Add("interrupted");
        if (output.Truncated)
            status.Add("embedded-nrepl/output-truncated");
        status.Add("done");
        await SendAsync(connection, Response(request, status.ToArray())).ConfigureAwait(false);
    }

    private void TryRelease(Session session)
    {
        lock (gate)
        {
            if (!session.Closing || session.Pending != 0 || session.Released)
                return;
            session.Released = true;
        }
        services.Sessions.Release(session.State);
    }

    private async Task<bool> SendAsync(Connection connection, Dictionary<string, object> response)
    {
        try
        {
            var bytes = BencodeEncoder.Encode(response, limits.MessageBytes);
            if (connection.Stream == null)
                return false;
            await connection.Stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException || error is SocketException ||
            error is ObjectDisposedException || error is BencodeException)
        {
            connection.Close();
            return false;
        }
    }

    private Dictionary<string, object> Describe(ReplRequest request)
    {
        var identity = services.Identity;
        var response = Response(request, new[] { "done" });
        response["ops"] = new Dictionary<string, object>
        {
            ["describe"] = new Dictionary<string, object>(),
            ["clone"] = new Dictionary<string, object>(),
            ["close"] = new Dictionary<string, object>(),
            ["ls-sessions"] = new Dictionary<string, object>(),
            ["eval"] = new Dictionary<string, object>()
        };
        response["versions"] = new Dictionary<string, object>
        {
            ["embedded-nrepl"] = Version(typeof(ReplServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion),
            ["clojure"] = Version(identity.LanguageVersion)
        };
        response["aux"] = new Dictionary<string, object>
        {
            ["productId"] = identity.ProductId,
            ["buildId"] = identity.BuildId,
            ["hostSessionId"] = identity.HostSessionId,
            ["runtimeGroupId"] = identity.RuntimeGroupId,
            ["serverInstanceId"] = Endpoint!.ServerInstanceId.ToString(),
            ["embedded-nrepl/host-product"] = identity.HostProduct,
            ["embedded-nrepl/host-version"] = identity.HostVersion,
            ["embedded-nrepl/host-build"] = identity.HostBuild,
            ["embedded-nrepl/adapter-target"] = identity.AdapterTarget,
            ["embedded-nrepl/runtime-version"] = RuntimeInformation.FrameworkDescription
        };
        return response;
    }

    private static Dictionary<string, object> Version(string value) => new Dictionary<string, object> { ["version-string"] = value };
    private static Dictionary<string, object> Response(ReplRequest request, string[] status)
    {
        var response = new Dictionary<string, object>(StringComparer.Ordinal);
        if (request.Id != null)
            response["id"] = request.Id;
        if (request.Session != null)
            response["session"] = request.Session;
        response["status"] = status;
        return response;
    }
    private static Dictionary<string, object> Response(IReadOnlyDictionary<string, object> request, string[] status)
    {
        var response = new Dictionary<string, object>(StringComparer.Ordinal);
        if (request.TryGetValue("id", out var id) && id is string)
            response["id"] = id;
        if (request.TryGetValue("session", out var session) && session is string)
            response["session"] = session;
        response["status"] = status;
        return response;
    }

    private sealed class Connection
    {
        private int closed;
        public Connection(TcpClient client)
        {
            Client = client;
        }
        public TcpClient Client
        {
            get;
        }
        public NetworkStream? Stream
        {
            get; set;
        }
        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
                Client.Dispose();
        }
    }

    private sealed class Session
    {
        public Session(IReplSessionState state, IReplSessionSnapshot published)
        {
            State = state;
            Published = published;
        }
        public string Id { get; } = Guid.NewGuid().ToString();
        public IReplSessionState State
        {
            get;
        }
        public IReplSessionSnapshot Published
        {
            get; set;
        }
        public int Pending;
        public bool Closing;
        public bool Released;
    }

    private sealed class CollectedOutput : IReplEvaluationOutput
    {
        private readonly ReplRequest request;
        private readonly int quota;
        private int bytes;
        public CollectedOutput(ReplRequest request, int quota)
        {
            this.request = request;
            this.quota = quota;
        }
        public List<Dictionary<string, object>> Responses { get; } = new List<Dictionary<string, object>>();
        public bool Truncated
        {
            get; private set;
        }
        public string? ErrorCode
        {
            get; private set;
        }
        public Action<int>? LateOutput => null;
        public bool TryWrite(ReplOutputKind kind, string text, int offset, int count)
            => Add(kind == ReplOutputKind.Out ? "out" : kind == ReplOutputKind.Error ? "err" : "value", text.Substring(offset, count), null, false);
        public bool TryWriteValue(string value, string ns, bool truncated) => Add("value", value, ns, truncated);
        private bool Add(string key, string value, string? ns, bool valueTruncated)
        {
            var size = Encoding.UTF8.GetByteCount(value);
            if (size > quota - bytes)
            {
                Truncated = true;
                return false;
            }
            bytes += size;
            var response = Response(request, Array.Empty<string>());
            response.Remove("status");
            response[key] = value;
            if (ns != null)
                response["ns"] = ReplEvaluationError.Limit(ns, 512);
            if (valueTruncated)
            {
                response["nrepl.middleware.print/truncated"] = new[] { "value" };
                Truncated = true;
            }
            Responses.Add(response);
            return true;
        }
        public void ReportError(ReplEvaluationError error)
        {
            ErrorCode = error.Code;
            var response = Response(request, Array.Empty<string>());
            response.Remove("status");
            response["ex"] = error.ExceptionType;
            response["root-ex"] = error.RootExceptionType;
            response["embedded-nrepl/phase"] = error.Phase;
            response["file"] = error.SourceName;
            response["line"] = error.Line;
            response["column"] = error.Column;
            response["err"] = error.Message + "\n";
            Responses.Add(response);
        }
        public void MarkTruncated() => Truncated = true;
        public void Flush()
        {
        }
    }
}
