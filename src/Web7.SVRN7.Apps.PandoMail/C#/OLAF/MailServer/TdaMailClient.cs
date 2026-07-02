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
        DateTime ReceivedAt);

    public sealed record EmailBody(
        string CorrelationId,
        string MessageDid,
        string Rfc5322Body,
        string BodyText);

    public sealed record DidResolutionResult(
        bool   Found,
        string RequestedDid,
        string Svrn7Name);

    /// <summary>
    /// PandoMail ↔ local Citizen TDA transport over WebSocket (ws://localhost:{port}/didcomm-ws).
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

        // Reconnect policy — ported from src/WsExample2-Kestrel's WSClient1 reconnect loop.
        private const int MaxRetries = 10;
        private static readonly TimeSpan RetryInterval  = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

        // Set by DisconnectAsync so an app-initiated close never triggers a background
        // reconnect loop — only an unexpected drop (server closed us, network blip) does.
        private volatile bool _intentionalDisconnect;

        // 0 = idle, 1 = a ReconnectLoopAsync is already running. Interlocked, not a lock,
        // since the only operation needed is "claim it once."
        private int _reconnecting;

        // Pending List-Emails requests keyed by correlationId → completion source.
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

        /// <summary>Fired on the thread-pool before each background reconnect attempt.</summary>
        public event Action<int, int> Reconnecting;

        /// <summary>Fired on the thread-pool once a background reconnect attempt succeeds.</summary>
        public event Action Reconnected;

        /// <summary>Fired on the thread-pool after MaxRetries consecutive failed reconnect attempts.</summary>
        public event Action ReconnectFailed;

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
            WsUri  = $"ws://localhost:{port}/didcomm-ws";
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
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            await SendHelloAsync(ct);
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
        /// Background reconnect loop ported from src/WsExample2-Kestrel's WSClient1:
        /// up to MaxRetries attempts, RetryInterval apart, each attempt getting a fresh
        /// ClientWebSocket (a closed/aborted one cannot be reused) and its own
        /// ConnectTimeout via ConnectCoreAsync. Stops early if DisconnectAsync was called
        /// or the client itself is being disposed.
        /// </summary>
        private async Task ReconnectLoopAsync()
        {
            try
            {
                for (int attempt = 1; attempt <= MaxRetries; attempt++)
                {
                    if (_intentionalDisconnect || _cts.IsCancellationRequested)
                        return;

                    _log.LogInformation("Reconnect attempt {Attempt}/{Max}", attempt, MaxRetries);
                    Reconnecting?.Invoke(attempt, MaxRetries);

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
                        _log.LogWarning(ex, "Reconnect attempt {Attempt}/{Max} failed", attempt, MaxRetries);
                    }

                    try { await Task.Delay(RetryInterval, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                }

                _log.LogWarning("Reconnect gave up after {Max} attempt(s)", MaxRetries);
                ReconnectFailed?.Invoke();
            }
            finally
            {
                Volatile.Write(ref _reconnecting, 0);
            }
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
            string correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new
                {
                    correlationId,
                    limit
                });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-Emails",
                    msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseEmailList(replyJson);
            }
            finally
            {
                _pending.TryRemove(correlationId, out _);
            }
        }

        // ── Outbound: Request outbox list ──────────────────────────────────────────

        public async Task<List<EmailSummary>> ListOutboundEmailsAsync(int limit = 50,
            CancellationToken ct = default)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { correlationId, limit });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-OutboundEmails",
                    msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseEmailList(replyJson);
            }
            finally
            {
                _pending.TryRemove(correlationId, out _);
            }
        }

        // ── Dead letters: Request dead-letter list ──────────────────────────────────

        public async Task<List<EmailSummary>> ListDeadLettersAsync(int limit = 50,
            CancellationToken ct = default)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { correlationId, limit });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-DeadLetters",
                    msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseEmailList(replyJson);
            }
            finally
            {
                _pending.TryRemove(correlationId, out _);
            }
        }

        // ── Inbound: Request full body of a single email ──────────────────────────

        public async Task<EmailBody> GetEmailBodyAsync(string messageDid, CancellationToken ct = default)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { correlationId, messageDid });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-EmailBody",
                    msgBody, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                timeout.Token.Register(() => tcs.TrySetCanceled());

                string replyJson = await tcs.Task;
                return ParseEmailBody(replyJson);
            }
            finally
            {
                _pending.TryRemove(correlationId, out _);
            }
        }

        // ── Inbound: Resolve a DID Document ────────────────────────────────────────

        public async Task<DidResolutionResult> ResolveDidAsync(string did, CancellationToken ct = default)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new { correlationId, requestedDid = did });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Resolve-PandoDid",
                    msgBody, ct);

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
                _pending.TryRemove(correlationId, out _);
            }
        }

        // ── Query: TDA's own DID ────────────────────────────────────────────────

        public async Task<string> GetTdaDidAsync(CancellationToken ct = default)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = tcs;

            try
            {
                string msgBody = JsonSerializer.Serialize(new
                {
                    correlationId
                });
                await SendEnvelopeAsync(
                    "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Query-TdaDid",
                    msgBody, ct);

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
                _pending.TryRemove(correlationId, out _);
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

        private async Task SendEnvelopeAsync(string type, string body, CancellationToken ct)
        {
            string envelope = JsonSerializer.Serialize(new
            {
                typ  = "application/didcomm-plain+json",
                id   = "did:drn:svrn7.net/didcomm/msg/" + Guid.NewGuid().ToString("N"),
                type,
                body
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
                    string cid = ExtractCorrelationId(root);
                    if (!string.IsNullOrEmpty(cid) && _pending.TryGetValue(cid, out var tcs))
                        tcs.TrySetResult(json);
                }
                else if (type.EndsWith("/Get-PandoMails", StringComparison.Ordinal))
                {
                    string cid = ExtractCorrelationId(root);
                    if (!string.IsNullOrEmpty(cid) && _pending.TryGetValue(cid, out var tcs))
                    {
                        tcs.TrySetResult(json);
                    }
                    else
                    {
                        // No correlationId match — complete the first pending request.
                        foreach (var kv in _pending)
                        {
                            kv.Value.TrySetResult(json);
                            break;
                        }
                    }
                }
                else if (type.EndsWith("/Get-PandoOutbox", StringComparison.Ordinal))
                {
                    string cid = ExtractCorrelationId(root);
                    if (!string.IsNullOrEmpty(cid) && _pending.TryGetValue(cid, out var tcs))
                        tcs.TrySetResult(json);
                    else
                    {
                        foreach (var kv in _pending) { kv.Value.TrySetResult(json); break; }
                    }
                }
                else if (type.EndsWith("/Get-PandoDeadLetters", StringComparison.Ordinal))
                {
                    string cid = ExtractCorrelationId(root);
                    if (!string.IsNullOrEmpty(cid) && _pending.TryGetValue(cid, out var tcs))
                        tcs.TrySetResult(json);
                    else
                    {
                        foreach (var kv in _pending) { kv.Value.TrySetResult(json); break; }
                    }
                }
                else if (type.EndsWith("/Reply-EmailBody", StringComparison.Ordinal))
                {
                    string cid = ExtractCorrelationId(root);
                    if (!string.IsNullOrEmpty(cid) && _pending.TryGetValue(cid, out var tcs))
                        tcs.TrySetResult(json);
                }
                else if (type.EndsWith("/Reply-DidDocument", StringComparison.Ordinal))
                {
                    string cid = ExtractCorrelationId(root);
                    _log.LogDebug("Reply-DidDocument correlationId={CorrelationId} pendingCount={PendingCount} matched={Matched}",
                        cid, _pending.Count, !string.IsNullOrEmpty(cid) && _pending.ContainsKey(cid));
                    if (!string.IsNullOrEmpty(cid) && _pending.TryGetValue(cid, out var tcs))
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

        private static string ExtractCorrelationId(JsonElement root)
        {
            if (!root.TryGetProperty("body", out JsonElement bodyEl)) return "";

            if (bodyEl.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using JsonDocument inner = JsonDocument.Parse(bodyEl.GetString()!);
                    return inner.RootElement.TryGetProperty("correlationId", out JsonElement c)
                        ? c.GetString() ?? "" : "";
                }
                catch { return ""; }
            }

            return bodyEl.TryGetProperty("correlationId", out JsonElement cv)
                ? cv.GetString() ?? "" : "";
        }

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
                    return new EmailBody(string.Empty, string.Empty, string.Empty, string.Empty);

                JsonElement resolved = bodyEl;
                if (bodyEl.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument inner = JsonDocument.Parse(bodyEl.GetString()!);
                    resolved = inner.RootElement.Clone();
                }

                return new EmailBody(
                    CorrelationId: GetStr(resolved, "correlationId"),
                    MessageDid:    GetStr(resolved, "messageDid"),
                    Rfc5322Body:   GetStr(resolved, "rfc5322Body"),
                    BodyText:      GetStr(resolved, "bodyText"));
            }
            catch { return new EmailBody(string.Empty, string.Empty, string.Empty, string.Empty); }
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
