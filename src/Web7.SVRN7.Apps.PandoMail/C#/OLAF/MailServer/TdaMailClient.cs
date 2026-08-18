using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Web7.SVRN7.Apps
{
    public sealed record EmailSummary(
        string   MessageDid,
        string   SenderDid,
        string   Subject,
        string   FromHeader,
        string   ToHeader,
        string   CcHeader,
        DateTime ReceivedAt);

    public sealed record EmailBody(
        string MessageDid,
        string Rfc5322Body,
        string BodyText);

    public sealed record DidResolutionResult(
        bool   Found,
        string RequestedDid,
        string Svrn7Name);

    /// <summary>
    /// PandoMail ↔ local Citizen TDA transport over WebSocket (ws://localhost:{port}/localcomm-ws).
    /// All outbound messages (Enqueue-PandoMail, List-Emails requests) go over the WebSocket.
    /// All inbound messages (Get-PandoMails replies, Email-Notify pushes) arrive over the same socket.
    /// TDA→TDA mail delivery remains HTTP/2 — this client is local-only.
    /// </summary>
    public sealed class TdaMailClient : IDisposable
    {
        private readonly string              _wsUri;
        private readonly HttpClient          _http;
        private ClientWebSocket              _ws;
        private readonly CancellationTokenSource _cts = new();
        private readonly ILogger<TdaMailClient> _log = AppLog.CreateLogger<TdaMailClient>();

        // Reconnect policy — the retry *mechanics* (fresh ClientWebSocket per attempt,
        // bounded ConnectTimeout via a linked token) are ported from
        // src/WsExample2-Kestrel's WSClient1. The *cadence* deliberately diverges: WSClient1
        // is a CLI tool the user is actively watching and can just restart, so it gives up
        // after 10 fixed-interval attempts (~10s). PandoMail is a background mail client
        // meant to behave like Outlook — it should keep quietly retrying, backing off, for
        // as long as the app is open, not go permanently dark after 10 seconds because the
        // TDA happened to be restarting for a LOBE deploy.
        private static readonly TimeSpan ConnectTimeout      = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ReconnectBaseDelay  = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ReconnectMaxDelay   = TimeSpan.FromSeconds(30);

        // Heartbeat: sent regardless of user activity so the TDA's idle watchdog
        // (WebSocketNotifyHub) sees this connection as alive even while the user is just
        // reading mail and generating no application requests. See docs/BACKLOG.md TDA-013.
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

        // A hard-killed server (crash, force-kill, process manager restart) sends no WS
        // close frame at all — a pending ws.ReceiveAsync can sit blocked far longer than any
        // of our own timeouts with no application-level signal that anything is wrong.
        // ReceiveTimeout (~3x HeartbeatInterval, so a missed Pong/Pong round trip or two is
        // tolerated) bounds how long we'll wait for *anything* to arrive — including the
        // server's Pong reply to our own Ping — before treating the connection as dead.
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(60);

        // Ticks, not DateTimeOffset directly — Interlocked has no DateTimeOffset overload,
        // and this is read/written from two different background loops.
        private long _lastReceivedTicks = DateTimeOffset.UtcNow.UtcTicks;

        // Set by DisconnectAsync so an app-initiated close never triggers a background
        // reconnect loop — only an unexpected drop (server closed us, network blip) does.
        private volatile bool _intentionalDisconnect;

        // 0 = idle, 1 = a ReconnectLoopAsync is already running. Interlocked, not a lock,
        // since the only operation needed is "claim it once."
        private int _reconnecting;

        // Pending requests keyed by this envelope's own 'id' → completion source. The
        // TDA's reply carries 'thid' == this id (DIDComm V2 thread correlation).
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

        // Stable across reconnects — lets the TDA's WebSocketNotifyHub and its logs
        // distinguish this process instance from another PandoMail window.
        private readonly Guid _instanceId = Guid.NewGuid();

        // Declared to the TDA via Hello so WebSocketNotifyHub knows which broadcast
        // notifications (as opposed to correlated request replies, which are always
        // unicast regardless of subscription) this connection should receive.
        private static readonly (string Uri, string Match)[] Subscriptions =
        {
            ("did:drn:svrn7.net/protocols/Email-Notify.0.1.0/", "prefix"),
            ("did:drn:svrn7.net/protocols/PandoMail.0.8.0/Notify-FolderCounts", "exact"),
        };

        /// <summary>Fired on the thread-pool when TDA pushes an Email-Notify envelope.</summary>
        public event Action<string> EmailNotifyReceived;

        /// <summary>Fired on the thread-pool when TDA pushes a Notify-FolderCounts envelope.</summary>
        public event Action<int, int, int> FolderCountsReceived;

        /// <summary>Fired on the thread-pool when the WebSocket connection drops unexpectedly.</summary>
        public event Action Disconnected;

        /// <summary>
        /// Fired on the thread-pool before each background reconnect attempt, with the
        /// attempt number and the backoff delay that follows if this attempt also fails.
        /// Retries indefinitely — there is no "gave up permanently" event.
        /// </summary>
        public event Action<int, TimeSpan> Reconnecting;

        /// <summary>Fired on the thread-pool once a background reconnect attempt succeeds.</summary>
        public event Action Reconnected;

        /// <summary>The connected TDA's agent DID, populated after GetTdaDidAsync() completes.</summary>
        public string TdaDid { get; private set; } = string.Empty;

        /// <summary>The connected TDA's Svrn7Name, populated after GetTdaDidAsync() completes.</summary>
        public string TdaName { get; private set; } = string.Empty;

        /// <summary>True when the WebSocket connection to the TDA is open.</summary>
        public bool IsConnected => _ws.State == WebSocketState.Open;

        /// <summary>The WebSocket URI this client connects to.</summary>
        public string WsUri { get; }

        public TdaMailClient(int port)
        {
            WsUri  = $"ws://localhost:{port}/localcomm-ws";
            _wsUri = WsUri;

            // Shared HttpClient for the ClientWebSocket HTTP/2 handshake (RFC 8441).
            var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
            _http = new HttpClient(handler)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionOrHigher
            };

            _ws = new ClientWebSocket();
            ConfigureWebSocket(_ws);
        }

        private static void ConfigureWebSocket(ClientWebSocket ws)
        {
            // Request WebSocket over HTTP/2 (RFC 8441 extended CONNECT).
            ws.Options.HttpVersion       = HttpVersion.Version20;
            ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        }

        // ── Connection lifecycle ────────────────────────────────────────────────

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            _intentionalDisconnect = false;
            try
            {
                await ConnectCoreAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("WS CONNECT timed out after {Timeout}", ConnectTimeout);
                throw;
            }
            catch (WebSocketException ex)
            {
                _log.LogWarning(ex, "WS CONNECT FAILED (WebSocketErrorCode={ErrorCode})", ex.WebSocketErrorCode);
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WS CONNECT FAILED ({ExceptionType})", ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Opens the socket, starts the receive loop, and sends Hello. Shared by the
        /// initial ConnectAsync and every background reconnect attempt. Bounds the
        /// connect itself to ConnectTimeout (5s) via a linked token, same as
        /// src/WsExample2-Kestrel's WSClient1 — a server that accepts the TCP connection
        /// but stalls the WebSocket upgrade must not hang the caller indefinitely.
        /// </summary>
        private async Task ConnectCoreAsync(CancellationToken ct)
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);

            _log.LogDebug("WS CONNECT {Uri}", _wsUri);
            await _ws.ConnectAsync(new Uri(_wsUri), _http, connectCts.Token);
            _log.LogInformation("WS CONNECT complete, state={State}", _ws.State);
            Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            await SendHelloAsync(ct);
            _ = Task.Run(() => HeartbeatLoopAsync(_ws, _cts.Token));
            _ = Task.Run(() => ReceiveWatchdogLoopAsync(_ws, _cts.Token));
        }

        /// <summary>
        /// Sends a Ping every HeartbeatInterval regardless of user activity, so the TDA's
        /// idle watchdog doesn't mistake "user is just reading mail" for a dead connection
        /// (see docs/BACKLOG.md TDA-013). Captures the specific ClientWebSocket this loop
        /// belongs to and stops quietly once a reconnect replaces _ws with a new instance
        /// (a fresh heartbeat loop starts for that new connection from ConnectCoreAsync) or
        /// the socket is no longer open — the receive loop is the authoritative disconnect
        /// detector; a failed Ping send just ends this loop, it doesn't drive reconnection.
        /// </summary>
        private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(HeartbeatInterval);
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (!ReferenceEquals(_ws, ws) || ws.State != WebSocketState.Open)
                        return;

                    try
                    {
                        string envelope = JsonSerializer.Serialize(new
                        {
                            typ  = "application/didcomm-plain+json",
                            id   = "did:drn:svrn7.net/didcomm/msg/" + Guid.NewGuid().ToString("N"),
                            type = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Ping",
                            body = new { instanceId = _instanceId.ToString() }
                        });
                        byte[] bytes = Encoding.UTF8.GetBytes(envelope);
                        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                        _log.LogDebug("sent Ping (heartbeat)");
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Ping send failed — connection is likely already gone; the receive loop will handle reconnect.");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Detects a peer that vanished without a graceful close — a hard-killed server
        /// sends no WS close frame, so a pending ws.ReceiveAsync in ReceiveLoopAsync can sit
        /// blocked far longer than any of our own timeouts with no application-level signal.
        /// If nothing at all has been received (including the server's Pong reply to our own
        /// Ping) for ReceiveTimeout, aborts the socket — Abort() forces the pending
        /// ReceiveAsync to fault immediately, which drives the existing Disconnected/
        /// reconnect path exactly as a real network error would. Same supersession guard as
        /// HeartbeatLoopAsync: stops quietly once a reconnect replaces _ws or the socket
        /// closes on its own.
        /// </summary>
        private async Task ReceiveWatchdogLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(HeartbeatInterval);
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (!ReferenceEquals(_ws, ws) || ws.State != WebSocketState.Open)
                        return;

                    var lastReceived = new DateTimeOffset(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);
                    var since = DateTimeOffset.UtcNow - lastReceived;
                    if (since > ReceiveTimeout)
                    {
                        _log.LogWarning(
                            "No data received for {Since}s (> {Timeout}s) — aborting connection to force reconnect.",
                            since.TotalSeconds, ReceiveTimeout.TotalSeconds);
                        ws.Abort();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Claims the reconnect loop if one isn't already running. Called from
        /// ReceiveLoopAsync's finally block on an unexpected disconnect — never after
        /// DisconnectAsync, and never after the initial ConnectAsync (that failure is
        /// still reported synchronously to the caller, unchanged).
        /// </summary>
        private void StartReconnectLoop()
        {
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
                return; // already reconnecting

            _ = ReconnectLoopAsync();
        }

        /// <summary>
        /// Background reconnect loop. Retries indefinitely with capped exponential backoff
        /// (1s, 2s, 4s, 8s, 16s, 30s, 30s, ...) — deliberately unbounded, unlike
        /// src/WsExample2-Kestrel's WSClient1 (10 fixed-interval attempts then gives up):
        /// PandoMail is a background mail client, not an actively-watched CLI tool, so it
        /// should keep quietly trying to reach the TDA for as long as it's running rather
        /// than go permanently dark after ~10 seconds. Each attempt gets a fresh
        /// ClientWebSocket (a closed/aborted one cannot be reused) and its own
        /// ConnectTimeout via ConnectCoreAsync. Stops early if DisconnectAsync was called
        /// or the client itself is being disposed.
        /// </summary>
        private async Task ReconnectLoopAsync()
        {
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    if (_intentionalDisconnect || _cts.IsCancellationRequested)
                        return;

                    var delay = ComputeBackoff(attempt);
                    _log.LogInformation("Reconnect attempt {Attempt} (next retry in {Delay}s if this fails)",
                        attempt, delay.TotalSeconds);
                    Reconnecting?.Invoke(attempt, delay);

                    try
                    {
                        _ws.Dispose();
                        _ws = new ClientWebSocket();
                        ConfigureWebSocket(_ws);
                        await ConnectCoreAsync(_cts.Token);
                        _log.LogInformation("Reconnected after {Attempt} attempt(s)", attempt);
                        Reconnected?.Invoke();
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Reconnect attempt {Attempt} failed — retrying in {Delay}s", attempt, delay.TotalSeconds);
                    }

                    try { await Task.Delay(delay, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
            finally
            {
                Volatile.Write(ref _reconnecting, 0);
            }
        }

        private static TimeSpan ComputeBackoff(int attempt)
        {
            double seconds = ReconnectBaseDelay.TotalSeconds * Math.Pow(2, attempt - 1);
            return TimeSpan.FromSeconds(Math.Min(seconds, ReconnectMaxDelay.TotalSeconds));
        }

        /// <summary>
        /// Declares this connection's identity and subscriptions to WebSocketNotifyHub.
        /// Sent as the last step of ConnectAsync, before any other traffic — the hub is
        /// fail-closed (see docs/BACKLOG.md TDA-011): a connection that hasn't sent Hello
        /// receives no broadcast notifications (correlated request replies are unaffected).
        /// </summary>
        private async Task SendHelloAsync(CancellationToken ct)
        {
            string appVersion = typeof(TdaMailClient).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";

            string envelope = JsonSerializer.Serialize(new
            {
                typ  = "application/didcomm-plain+json",
                id   = "did:drn:svrn7.net/didcomm/msg/" + Guid.NewGuid().ToString("N"),
                type = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Hello",
                body = new
                {
                    app           = "PandoMail",
                    appVersion,
                    appFullName   = typeof(TdaMailClient).Assembly.GetName().FullName,
                    instanceId    = _instanceId.ToString(),
                    mvid          = typeof(TdaMailClient).Module.ModuleVersionId.ToString(),
                    subscriptions = Subscriptions.Select(s => new { uri = s.Uri, match = s.Match })
                }
            });
            byte[] bytes = Encoding.UTF8.GetBytes(envelope);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            _log.LogDebug("sent Hello: {Envelope}", envelope);
        }

        /// <summary>
        /// Sends Goodbye and closes the WebSocket cleanly. Best-effort — the WS close
        /// frame is authoritative for connection teardown either way, so a failure here
        /// (e.g. TDA already gone) is not treated as an error.
        /// </summary>
        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            _intentionalDisconnect = true;
            if (_ws.State != WebSocketState.Open) return;
            try
            {
                string envelope = JsonSerializer.Serialize(new
                {
                    typ  = "application/didcomm-plain+json",
                    id   = "did:drn:svrn7.net/didcomm/msg/" + Guid.NewGuid().ToString("N"),
                    type = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Goodbye",
                    body = new { instanceId = _instanceId.ToString() }
                });
                byte[] bytes = Encoding.UTF8.GetBytes(envelope);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client disconnecting", ct);
            }
            catch { /* best-effort */ }
        }

        // ── Outbound: Send a composed email ────────────────────────────────────

        /// <param name="recipientDid">Semicolon-separated for multiple To recipients.</param>
        /// <param name="cc">Semicolon-separated Cc recipient DIDs; empty when there are none.</param>
        public async Task SendAsync(string recipientDid, string subject, string bodyText,
            string senderDisplay = "", string recipientDisplay = "",
            string cc = "", string ccDisplay = "",
            CancellationToken ct = default)
        {
            string msgBody = JsonSerializer.Serialize(new
                { recipientDid, subject, bodyText, senderDisplay, recipientDisplay, cc, ccDisplay });
            await SendEnvelopeAsync(
                "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Enqueue-PandoMail",
                msgBody, ct);
        }

        // ── Inbound: Request current email list ────────────────────────────────

        public async Task<List<EmailSummary>> ListEmailsAsync(int limit = 50,
            CancellationToken ct = default)
        {
            string id = NewMessageId();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { limit });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-Emails",
                    id, msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseEmailList(replyJson);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // ── Outbound: Request outbox list ──────────────────────────────────────────

        public async Task<List<EmailSummary>> ListOutboundEmailsAsync(int limit = 50,
            CancellationToken ct = default)
        {
            string id = NewMessageId();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { limit });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-OutboundEmails",
                    id, msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseEmailList(replyJson);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // ── Dead letters: Request dead-letter list ──────────────────────────────────

        public async Task<List<EmailSummary>> ListDeadLettersAsync(int limit = 50,
            CancellationToken ct = default)
        {
            string id = NewMessageId();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { limit });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-DeadLetters",
                    id, msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseEmailList(replyJson);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // ── Inbound: Request full body of a single email ──────────────────────────

        public async Task<EmailBody> GetEmailBodyAsync(string messageDid, CancellationToken ct = default)
        {
            string id = NewMessageId();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { messageDid });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-EmailBody",
                    id, msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseEmailBody(replyJson);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // ── Inbound: Resolve a DID Document ────────────────────────────────────────

        public async Task<DidResolutionResult> ResolveDidAsync(string did, CancellationToken ct = default)
        {
            string id = NewMessageId();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { requestedDid = did });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Resolve-PandoDid",
                    id, msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseDidResolution(replyJson);
            }
            catch (OperationCanceledException)
            {
                return new DidResolutionResult(false, did, null);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // ── Query: TDA's own DID ────────────────────────────────────────────────

        public async Task<string> GetTdaDidAsync(CancellationToken ct = default)
        {
            string id = NewMessageId();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Query-TdaDid",
                    id, "{}", ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                TdaDid  = ParseTdaDid(replyJson);
                TdaName = ParseTdaName(replyJson);
                return TdaDid;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // ── Startup: request current folder counts ─────────────────────────────

        public async Task RequestFolderCountsAsync(CancellationToken ct = default)
        {
            await SendEnvelopeAsync(
                "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Query-FolderCounts",
                "{}", ct);
        }

        // ── Core send ───────────────────────────────────────────────────────────

        private static string NewMessageId() => "did:drn:svrn7.net/didcomm/msg/" + Guid.NewGuid().ToString("N");

        /// <summary>Fire-and-forget send — no reply correlation needed (e.g. Enqueue-PandoMail, Query-FolderCounts).</summary>
        private Task SendEnvelopeAsync(string type, string body, CancellationToken ct) =>
            SendEnvelopeAsync(type, NewMessageId(), body, ct);

        /// <summary>
        /// Sends a request using <paramref name="id"/> as this envelope's DIDComm 'id'.
        /// Callers awaiting a correlated reply generate this id themselves, register it in
        /// _pending *before* calling this method, and match the reply by its 'thid' (which
        /// the TDA sets to this id) — see docs/BACKLOG.md TDA-014.
        /// </summary>
        /// <param name="body">
        /// Pre-serialized JSON text (e.g. from JsonSerializer.Serialize(new {...})). Parsed
        /// back into a JsonElement here so it's embedded in the envelope as a raw JSON object
        /// per the DIDComm v2 spec ("body... MUST be a JSON object") — not as a string-typed
        /// property, which would double-encode it (e.g. "body":"{\"limit\":50}" instead of
        /// "body":{"limit":50}).
        /// </param>
        private async Task SendEnvelopeAsync(string type, string id, string body, CancellationToken ct)
        {
            using JsonDocument bodyDoc = JsonDocument.Parse(body);
            string envelope = JsonSerializer.Serialize(new
            {
                typ = "application/didcomm-plain+json",
                id,
                type,
                body = bodyDoc.RootElement
            });
            byte[] bytes = Encoding.UTF8.GetBytes(envelope);
            _log.LogDebug("WS SEND type={Type} bytes={Bytes} state={State}", type, bytes.Length, _ws.State);
            try
            {
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                _log.LogDebug("WS SEND complete type={Type}", type);
            }
            catch (WebSocketException ex)
            {
                _log.LogWarning(ex, "WS SEND FAILED type={Type} (WebSocketErrorCode={ErrorCode})", type, ex.WebSocketErrorCode);
                throw;
            }
        }

        // ── Receive loop ────────────────────────────────────────────────────────

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
                    var recvJson = Encoding.UTF8.GetString(ms.ToArray());
                    _log.LogDebug("WS RECV {Bytes} bytes: {Json}", ms.Length, recvJson);
                    DispatchReceived(recvJson);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    Disconnected?.Invoke();
                    if (!_intentionalDisconnect)
                        StartReconnectLoop();
                }
            }
        }

        private void DispatchReceived(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string type = root.TryGetProperty("type", out JsonElement t)
                    ? t.GetString() ?? "" : "";

                _log.LogDebug("WS DISPATCH type={Type}", type);

                if (type.EndsWith("/Reply-TdaDid", StringComparison.Ordinal))
                {
                    string thid = ExtractThid(root);
                    if (!string.IsNullOrEmpty(thid) && _pending.TryGetValue(thid, out var tcs))
                        tcs.TrySetResult(json);
                }
                else if (type.EndsWith("/Get-PandoMails", StringComparison.Ordinal))
                {
                    string thid = ExtractThid(root);
                    if (!string.IsNullOrEmpty(thid) && _pending.TryGetValue(thid, out var tcs))
                    {
                        tcs.TrySetResult(json);
                    }
                    else
                    {
                        // No thid match — complete the first pending request.
                        foreach (var kv in _pending)
                        {
                            kv.Value.TrySetResult(json);
                            break;
                        }
                    }
                }
                else if (type.EndsWith("/Get-PandoOutbox", StringComparison.Ordinal))
                {
                    string thid = ExtractThid(root);
                    if (!string.IsNullOrEmpty(thid) && _pending.TryGetValue(thid, out var tcs))
                        tcs.TrySetResult(json);
                    else
                    {
                        foreach (var kv in _pending) { kv.Value.TrySetResult(json); break; }
                    }
                }
                else if (type.EndsWith("/Get-PandoDeadLetters", StringComparison.Ordinal))
                {
                    string thid = ExtractThid(root);
                    if (!string.IsNullOrEmpty(thid) && _pending.TryGetValue(thid, out var tcs))
                        tcs.TrySetResult(json);
                    else
                    {
                        foreach (var kv in _pending) { kv.Value.TrySetResult(json); break; }
                    }
                }
                else if (type.EndsWith("/Reply-EmailBody", StringComparison.Ordinal))
                {
                    string thid = ExtractThid(root);
                    if (!string.IsNullOrEmpty(thid) && _pending.TryGetValue(thid, out var tcs))
                        tcs.TrySetResult(json);
                }
                else if (type.EndsWith("/Reply-DidDocument", StringComparison.Ordinal))
                {
                    string thid = ExtractThid(root);
                    _log.LogDebug("Reply-DidDocument thid={Thid} pendingCount={PendingCount} matched={Matched}",
                        thid, _pending.Count, !string.IsNullOrEmpty(thid) && _pending.ContainsKey(thid));
                    if (!string.IsNullOrEmpty(thid) && _pending.TryGetValue(thid, out var tcs))
                        tcs.TrySetResult(json);
                }
                else if (type.EndsWith("/Notify-FolderCounts", StringComparison.Ordinal))
                {
                    var (inbox, sent, dead) = ParseFolderCounts(json);
                    FolderCountsReceived?.Invoke(inbox, sent, dead);
                }
                else if (type.EndsWith("/new-message", StringComparison.Ordinal) ||
                         type.Contains("Email-Notify", StringComparison.OrdinalIgnoreCase))
                {
                    EmailNotifyReceived?.Invoke(json);
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads the envelope-level DIDComm 'thid' — the TDA sets this to the original
        /// request's 'id' when replying, per DIDComm V2's standard thread-correlation
        /// mechanism (see docs/BACKLOG.md TDA-014). No body parsing needed.
        /// </summary>
        private static string ExtractThid(JsonElement root) =>
            root.TryGetProperty("thid", out JsonElement thidEl) && thidEl.ValueKind == JsonValueKind.String
                ? thidEl.GetString() ?? ""
                : "";

        // ── Parsing ─────────────────────────────────────────────────────────────

        private static DidResolutionResult ParseDidResolution(string envelopeJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(envelopeJson);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("body", out JsonElement bodyEl))
                    return new DidResolutionResult(false, string.Empty, null);

                JsonElement resolved = bodyEl;
                if (bodyEl.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument inner = JsonDocument.Parse(bodyEl.GetString()!);
                    resolved = inner.RootElement.Clone();
                }

                bool found = resolved.TryGetProperty("found", out JsonElement foundEl) && foundEl.GetBoolean();
                string requestedDid = GetStr(resolved, "requestedDid");
                string svrn7Name = resolved.TryGetProperty("svrn7Name", out JsonElement nameEl)
                    ? nameEl.GetString() ?? string.Empty : string.Empty;

                return new DidResolutionResult(found, requestedDid, svrn7Name);
            }
            catch { return new DidResolutionResult(false, string.Empty, null); }
        }

        private static string ParseTdaDid(string envelopeJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(envelopeJson);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("body", out JsonElement bodyEl)) return string.Empty;

                JsonElement resolved = bodyEl;
                if (bodyEl.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument inner = JsonDocument.Parse(bodyEl.GetString()!);
                    resolved = inner.RootElement.Clone();
                }

                return resolved.TryGetProperty("did", out JsonElement didEl)
                    ? didEl.GetString() ?? string.Empty : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string ParseTdaName(string envelopeJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(envelopeJson);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("body", out JsonElement bodyEl)) return string.Empty;

                JsonElement resolved = bodyEl;
                if (bodyEl.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument inner = JsonDocument.Parse(bodyEl.GetString()!);
                    resolved = inner.RootElement.Clone();
                }

                return resolved.TryGetProperty("name", out JsonElement nameEl)
                    ? nameEl.GetString() ?? string.Empty : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static (int Inbox, int Sent, int DeadLetters) ParseFolderCounts(string envelopeJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(envelopeJson);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("body", out JsonElement bodyEl)) return (0, 0, 0);

                JsonElement resolved = bodyEl;
                if (bodyEl.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument inner = JsonDocument.Parse(bodyEl.GetString()!);
                    resolved = inner.RootElement.Clone();
                }

                int inbox = resolved.TryGetProperty("inboxCount",      out JsonElement ic) ? ic.GetInt32() : 0;
                int sent  = resolved.TryGetProperty("sentCount",       out JsonElement sc) ? sc.GetInt32() : 0;
                int dead  = resolved.TryGetProperty("deadLetterCount", out JsonElement dc) ? dc.GetInt32() : 0;
                return (inbox, sent, dead);
            }
            catch { return (0, 0, 0); }
        }

        private static List<EmailSummary> ParseEmailList(string envelopeJson)
        {
            var result = new List<EmailSummary>();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(envelopeJson);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("body", out JsonElement bodyEl)) return result;

                JsonElement resolved = bodyEl;
                if (bodyEl.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument inner = JsonDocument.Parse(bodyEl.GetString()!);
                    resolved = inner.RootElement.Clone();
                }

                if (!resolved.TryGetProperty("emails", out JsonElement emailsEl)) return result;

                foreach (JsonElement e in emailsEl.EnumerateArray())
                {
                    result.Add(new EmailSummary(
                        MessageDid:  GetStr(e, "messageDid"),
                        SenderDid:   GetStr(e, "senderDid"),
                        Subject:     GetStrOrNull(e, "subject"),
                        FromHeader:  GetStrOrNull(e, "fromHeader"),
                        ToHeader:    GetStrOrNull(e, "toHeader"),
                        CcHeader:    GetStrOrNull(e, "ccHeader"),
                        ReceivedAt:  e.TryGetProperty("receivedAt", out JsonElement rv)
                                     && DateTime.TryParse(rv.GetString(), out DateTime dt)
                                     ? dt.ToLocalTime() : DateTime.Now));
                }
            }
            catch { }
            return result;
        }

        private static EmailBody ParseEmailBody(string envelopeJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(envelopeJson);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("body", out JsonElement bodyEl))
                    return new EmailBody(string.Empty, string.Empty, string.Empty);

                JsonElement resolved = bodyEl;
                if (bodyEl.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument inner = JsonDocument.Parse(bodyEl.GetString()!);
                    resolved = inner.RootElement.Clone();
                }

                return new EmailBody(
                    MessageDid:  GetStr(resolved, "messageDid"),
                    Rfc5322Body: GetStr(resolved, "rfc5322Body"),
                    BodyText:    GetStr(resolved, "bodyText"));
            }
            catch { return new EmailBody(string.Empty, string.Empty, string.Empty); }
        }

        private static string GetStr(JsonElement el, string name) =>
            el.TryGetProperty(name, out JsonElement v) ? v.GetString() ?? string.Empty : string.Empty;

        private static string GetStrOrNull(JsonElement el, string name) =>
            el.TryGetProperty(name, out JsonElement v) ? v.GetString() ?? string.Empty : string.Empty;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _ws.Dispose();
            _http.Dispose();
        }
    }
}
