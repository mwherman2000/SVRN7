using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Svrn7.TDA;

/// <summary>
/// Hub for local-UI WebSocket connections on /didcomm-ws.
/// Supports multiple simultaneous clients (PandoMail, PS tooling, etc.).
///
/// When a LOBE returns an OutboundMessage whose PeerEndpoint matches
/// <see cref="LocalEndpoint"/>, the Switchboard calls <see cref="PushAsync"/>
/// instead of making an outbound HTTP/2 POST. The LOBE itself is unaware
/// of the transport — only the Switchboard delivery path differs.
///
/// Delivery is two-tiered (see docs/BACKLOG.md TDA-011):
///   1. Correlated replies (body.correlationId matches a tracked request) are
///      unicast to the connection that made the original request.
///   2. Everything else is multicast to connections whose declared Hello
///      subscriptions match the message's @type (exact or prefix).
/// A connection that has not sent Hello has no subscriptions and receives
/// nothing via tier 2 — fail-closed by design, no unfiltered fallback.
/// </summary>
public sealed class WebSocketNotifyHub : IDisposable
{
    /// <summary>
    /// Sentinel PeerEndpoint value: any OutboundMessage targeting this endpoint
    /// is delivered via WebSocket push rather than HTTP/2 POST.
    /// </summary>
    public const string LocalEndpoint = "ws://local/didcomm-ws";

    // ── Svrn7.LocalUI.0.1.0 control-plane protocol ───────────────────────────
    // Connection-lifecycle messages. Intercepted in TryHandleControlFrameAsync
    // before the inbox/Switchboard ever sees them — LOBE cmdlets have no notion
    // of which socket a message arrived on, so subscription bookkeeping cannot
    // be a LOBE-routed protocol.
    private const string HelloType      = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Hello";
    private const string GoodbyeType    = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Goodbye";
    private const string SubscribedType = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Subscribed";
    private const string TimeoutType    = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Timeout";
    private const string PingType       = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Ping";
    private const string PongType       = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Pong";

    /// <summary>Per-frame receive cap — mirrors the reference design in src/WsExample2-Kestrel.</summary>
    public const int MaxMessageBytes = 1 * 1024 * 1024;

    // IdleTimeout deliberately diverges from src/WsExample2-Kestrel's 15s: that value fits
    // an echo-test client with continuous traffic, not a mail client whose whole point is to
    // sit quietly for minutes at a time while the user reads. TdaMailClient now sends a Ping
    // heartbeat every 20s specifically to keep real connections well under this threshold —
    // IdleTimeout here is a generous backstop for connections that never heartbeat at all
    // (crashed clients, or tools like Send-LocalDIDCommMessage that connect once and leave),
    // not the primary liveness mechanism.
    private static readonly TimeSpan IdleTimeout      = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CorrelationTtl   = TimeSpan.FromMinutes(5);

    private readonly ILogger<WebSocketNotifyHub> _log;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, PendingCorrelation> _pendingCorrelations = new();
    private readonly Timer _watchdogTimer;

    public WebSocketNotifyHub(ILogger<WebSocketNotifyHub> log)
    {
        _log = log;
        _watchdogTimer = new Timer(_ => CloseIdleConnections(), null, WatchdogInterval, WatchdogInterval);
    }

    public bool IsConnected =>
        _connections.Values.Any(c => c.Socket.State == WebSocketState.Open);

    internal Guid Attach(WebSocket ws)
    {
        var id = Guid.NewGuid();
        _connections[id] = new Connection(ws);
        return id;
    }

    internal void Detach(Guid id)
    {
        if (_connections.TryRemove(id, out var conn))
            conn.SendLock.Dispose();
    }

    /// <summary>Resets the idle-watchdog clock for a connection. Call on every received frame.</summary>
    internal void MarkReceived(Guid id)
    {
        if (_connections.TryGetValue(id, out var conn))
            conn.LastReceived = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Associates a request's correlationId with the socket that sent it, so the eventual
    /// reply (e.g. Get-PandoMails, Reply-TdaDid) can be routed back to that connection alone
    /// instead of broadcast to every connected local-UI client. Call before enqueueing an
    /// inbound WebSocket message whose body carries a correlationId.
    /// </summary>
    internal void TrackCorrelation(string correlationId, Guid socketId)
    {
        var now = DateTimeOffset.UtcNow;
        _pendingCorrelations[correlationId] = new PendingCorrelation(socketId, now);

        // Opportunistic cleanup — bounded by CorrelationTtl, no background timer needed.
        // Catches requests that never got a reply (dead-lettered, LOBE bug, etc.).
        foreach (var (key, value) in _pendingCorrelations)
        {
            if (now - value.RecordedAt > CorrelationTtl)
                _pendingCorrelations.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Intercepts Svrn7.LocalUI.0.1.0/Hello and .../Goodbye frames before they would
    /// otherwise enter the inbox/Switchboard pipeline. Returns true if the frame was a
    /// control message — the caller must not enqueue it.
    /// </summary>
    internal async Task<bool> TryHandleControlFrameAsync(Guid id, string json, CancellationToken ct)
    {
        string type;
        JsonElement body;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            body = ExtractBody(root);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WebSocketNotifyHub: could not parse WebSocket frame as JSON — not a control frame.");
            return false;
        }

        if (type == HelloType)
        {
            if (!_connections.TryGetValue(id, out var conn)) return true;

            conn.App        = GetString(body, "app");
            conn.AppVersion = GetString(body, "appVersion");
            conn.InstanceId = GetString(body, "instanceId");

            conn.Subscriptions.Clear();
            if (body.ValueKind == JsonValueKind.Object &&
                body.TryGetProperty("subscriptions", out var subs) &&
                subs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in subs.EnumerateArray())
                {
                    var uri   = GetString(s, "uri");
                    var match = GetString(s, "match");
                    if (!string.IsNullOrEmpty(uri))
                        conn.Subscriptions.Add((uri, match == "exact" ? "exact" : "prefix"));
                }
            }

            _log.LogInformation(
                "WebSocketNotifyHub: Hello from app='{App}' version='{Version}' instance={Instance} " +
                "({Count} subscription(s)).",
                conn.App, conn.AppVersion, conn.InstanceId, conn.Subscriptions.Count);

            var ack = JsonSerializer.Serialize(new
            {
                typ  = "application/didcomm-plain+json",
                id   = Svrn7.Core.TdaResourceId.DIDCommMessage(Guid.NewGuid().ToString("N")),
                type = SubscribedType,
                body = new
                {
                    subscriptions = conn.Subscriptions.Select(s => new { uri = s.Uri, match = s.Match })
                }
            });
            await SendToConnectionAsync(id, ack, ct);
            return true;
        }

        if (type == GoodbyeType)
        {
            if (_connections.TryGetValue(id, out var conn))
                _log.LogInformation(
                    "WebSocketNotifyHub: Goodbye from app='{App}' instance={Instance}.",
                    conn.App, conn.InstanceId);
            return true;
        }

        if (type == PingType)
        {
            // KestrelListenerService already called MarkReceived before this method ran —
            // that alone is enough to keep the server's own idle clock fresh. The Pong reply
            // is for the *client's* benefit: TdaMailClient tracks time-since-last-received
            // and needs proof the server is actually still there, not just that its own send
            // succeeded — a hard-killed server (vs. a graceful close) sends no close frame at
            // all, so without a reply the client would have nothing to notice a dead peer by
            // except an unrelated user action failing. No per-Ping/Pong log at Information —
            // the generic frame-received/processing Debug lines already cover it.
            var pong = JsonSerializer.Serialize(new
            {
                typ  = "application/didcomm-plain+json",
                id   = Svrn7.Core.TdaResourceId.DIDCommMessage(Guid.NewGuid().ToString("N")),
                type = PongType
            });
            await SendToConnectionAsync(id, pong, ct);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Pushes a DIDComm JSON envelope to connected local-UI clients.
    /// Correlated replies (body.correlationId matches a tracked request) are unicast to the
    /// requesting connection. Everything else is multicast to connections whose declared
    /// subscriptions match the message's @type. No-op if no clients are connected.
    /// </summary>
    public async Task PushAsync(string json, CancellationToken ct = default)
    {
        if (_connections.IsEmpty) return;

        string type = "(unknown)";
        string? correlationId = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t)) type = t.GetString() ?? type;
            var body = ExtractBody(root);
            var cid = GetString(body, "correlationId");
            correlationId = string.IsNullOrEmpty(cid) ? null : cid;
        }
        catch { }

        if (correlationId is not null && _pendingCorrelations.TryRemove(correlationId, out var pending))
        {
            _log.LogDebug(
                "WebSocketNotifyHub: → connection {Id} (correlated reply) type={Type}",
                pending.SocketId, type);
            await SendToConnectionAsync(pending.SocketId, json, ct);
            return;
        }

        foreach (var (id, conn) in _connections.ToArray())
        {
            if (conn.Socket.State != WebSocketState.Open)
            {
                _connections.TryRemove(id, out _);
                continue;
            }
            if (!MatchesSubscription(conn.Subscriptions, type)) continue;

            _log.LogDebug("WebSocketNotifyHub: → connection {Id} type={Type}", id, type);
            await SendToConnectionAsync(id, json, ct);
        }

        _log.LogDebug("WebSocketNotifyHub: push complete type={Type}.", type);
    }

    private async Task SendToConnectionAsync(Guid id, string json, CancellationToken ct)
    {
        if (!_connections.TryGetValue(id, out var conn) || conn.Socket.State != WebSocketState.Open)
            return;

        var bytes = Encoding.UTF8.GetBytes(json);
        await conn.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await conn.Socket.SendAsync(
                new ReadOnlyMemory<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            _log.LogDebug(ex, "WebSocketNotifyHub: send failed for connection {Id} — will be pruned.", id);
        }
        finally
        {
            conn.SendLock.Release();
        }
    }

    private static bool MatchesSubscription(List<(string Uri, string Match)> subscriptions, string type)
    {
        foreach (var (uri, match) in subscriptions)
        {
            if (match == "exact")
            {
                if (type.Equals(uri, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (type.StartsWith(uri, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ── Idle watchdog ─────────────────────────────────────────────────────────
    // A single shared timer (matching IsolatedRunspaceFactory's epoch-refresh pattern)
    // rather than one Task.Run per connection — simpler for the connection counts this
    // channel actually sees (a handful of local-UI apps, not high-volume peers).

    private void CloseIdleConnections()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, conn) in _connections.ToArray())
        {
            if (conn.Socket.State != WebSocketState.Open) continue;
            if (now - conn.LastReceived <= IdleTimeout) continue;

            _log.LogInformation(
                "WebSocketNotifyHub: connection {Id} idle for over {Seconds}s — closing.",
                id, IdleTimeout.TotalSeconds);
            _ = CloseIdleConnectionAsync(id, conn);
        }
    }

    private async Task CloseIdleConnectionAsync(Guid id, Connection conn)
    {
        try
        {
            var notice = JsonSerializer.Serialize(new
            {
                typ  = "application/didcomm-plain+json",
                id   = Svrn7.Core.TdaResourceId.DIDCommMessage(Guid.NewGuid().ToString("N")),
                type = TimeoutType
            });
            await SendToConnectionAsync(id, notice, CancellationToken.None);

            // Half-close: send the close frame and rely on the client responding with its
            // own, which lets KestrelListenerService's ReceiveAsync loop end and Detach
            // naturally. No hard-cancel fallback — this channel serves a small, trusted set
            // of first-party local processes, not adversarial peers (see BACKLOG.md TDA-013).
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "idle timeout", closeCts.Token);
        }
        catch { /* best-effort — the receive loop's finally block detaches on its own close/error */ }
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────
    // Envelope bodies are sometimes a nested JSON object and sometimes a JSON-encoded
    // string (existing inconsistency across LOBEs/clients) — handle both, matching the
    // pattern already used in TdaMailClient.ExtractCorrelationId.

    private static JsonElement ExtractBody(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var bodyEl)) return default;
        if (bodyEl.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var inner = JsonDocument.Parse(bodyEl.GetString() ?? "{}");
                return inner.RootElement.Clone();
            }
            catch { return default; }
        }
        // Clone — callers (e.g. TryHandleControlFrameAsync) use the returned element after
        // the source JsonDocument (owned by the caller's `using`) has already been disposed.
        return bodyEl.Clone();
    }

    private static string GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
    }

    public void Dispose() => _watchdogTimer.Dispose();

    // ── Per-connection state ──────────────────────────────────────────────────

    private sealed class Connection
    {
        public Connection(WebSocket socket) => Socket = socket;

        public WebSocket Socket { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public List<(string Uri, string Match)> Subscriptions { get; } = new();
        public DateTimeOffset LastReceived { get; set; } = DateTimeOffset.UtcNow;
        public string App { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public string InstanceId { get; set; } = "";
    }

    private readonly record struct PendingCorrelation(Guid SocketId, DateTimeOffset RecordedAt);
}
