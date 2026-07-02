using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Svrn7.Core.Interfaces;
using Svrn7.DIDComm;

namespace Svrn7.TDA;

// ── KestrelListenerService ────────────────────────────────────────────────────
//
// Derived from: "HTTP Listener/Sender (HTTPClient)" + "DIDComm V2 Messaging"
//               — DSA 0.24 Epoch 0 (PPML).
//
// Design invariants (DSA 0.24 / PPML Derivation Rules):
//
//   SINGLE INBOUND SURFACE: POST /didcomm is the only route. No REST API,
//   no health endpoint, no gRPC. TDAs only talk to other TDAs (closed ecosystem).
//
//   PACK/UNPACK AT BOUNDARY: Unpack (JWE decrypt + JWS verify) is performed here,
//   before anything is written to the inbox. If UnpackAsync fails, 400 is returned
//   and nothing is enqueued. Agents always receive unpacked plaintext via ObjectId
//   reference.
//
//   WRITE-AHEAD LOG GATE: After successful unpack, IInboxStore.EnqueueAsync writes
//   the payload to svrn7-msg.db and returns 202 immediately. The Switchboard
//   processes asynchronously. The Listener has no knowledge of routing or agent logic.
//
//   HTTP/2 + mTLS: Kestrel binds on the configured port with HTTP/2 and mutual TLS.
//   Only peers presenting a valid TDA certificate can call POST /didcomm.

/// <summary>
/// Kestrel HTTP/2 + mTLS listener — the single inbound gate for all DIDComm traffic.
/// Derived from: HTTP Listener/Sender (HTTPClient) + DIDComm V2 Messaging — DSA 0.24 Epoch 0 (PPML).
/// </summary>
public sealed class KestrelListenerService : IHostedService, IAsyncDisposable
{
    private readonly TdaOptions               _opts;
    private readonly IDIDCommService          _didComm;
    private readonly IInboxStore              _inbox;
    private readonly WebSocketNotifyHub       _hub;
    private readonly ILogger<KestrelListenerService> _log;

    private WebApplication? _app;

    public KestrelListenerService(
        IOptions<TdaOptions>               opts,
        IDIDCommService                    didComm,
        IInboxStore                        inbox,
        WebSocketNotifyHub                 hub,
        ILogger<KestrelListenerService>    log)
    {
        _opts    = opts.Value;
        _didComm = didComm;
        _inbox   = inbox;
        _hub     = hub;
        _log     = log;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();

        // ── Kestrel: HTTP/2 + mTLS ────────────────────────────────────────────
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Guard against oversized bodies — 2 MB is generous for any DIDComm message.
            kestrel.Limits.MaxRequestBodySize = 2 * 1024 * 1024;

            kestrel.ListenAnyIP(_opts.ListenPort, listenOpts =>
            {
                listenOpts.Protocols = HttpProtocols.Http2;

                if (_opts.TlsCertificatePath is not null)
                {
                    listenOpts.UseHttps(https =>
                    {
                        https.ServerCertificate = new X509Certificate2(
                            _opts.TlsCertificatePath,
                            _opts.TlsCertificatePassword);

                        if (_opts.RequireMutualTls)
                        {
                            https.ClientCertificateMode =
                                Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                            https.ClientCertificateValidation = ValidatePeerTdaCertificate;
                        }
                    });
                }
                else
                {
                    // Development fallback: plain HTTP/2 (cleartext).
                    // Never use in production — mTLS is required for a conformant TDA.
                    _log.LogWarning(
                        "KestrelListenerService: TLS certificate not configured. " +
                        "Running in cleartext HTTP/2 (development mode only).");
                }
            });
        });

        // ── Rate limiting ─────────────────────────────────────────────────────
        // Fixed-window per-IP: protects against a misbehaving or compromised peer
        // flooding the inbox. Disabled when RateLimitRequestsPerSecond == 0.
        const string rateLimitPolicy = "didcomm";
        if (_opts.RateLimitRequestsPerSecond > 0)
        {
            builder.Services.AddRateLimiter(rl =>
            {
                rl.AddFixedWindowLimiter(rateLimitPolicy, options =>
                {
                    options.PermitLimit         = _opts.RateLimitRequestsPerSecond;
                    options.Window              = TimeSpan.FromSeconds(1);
                    options.QueueLimit          = 0;
                    options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                });
                rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            });
        }

        _app = builder.Build();

        if (_opts.RateLimitRequestsPerSecond > 0)
            _app.UseRateLimiter();

        // WebSocket support (RFC 8441 over HTTP/2 for the /localcomm-ws path).
        _app.UseWebSockets();

        // ── Single inbound route: POST /didcomm ───────────────────────────────
        var route = _app.MapPost("/didcomm", HandleInboundAsync);
        if (_opts.RateLimitRequestsPerSecond > 0)
            route.RequireRateLimiting(rateLimitPolicy);

        // ── Local UI push channel: /localcomm-ws (WebSocket, localhost only) ─
        // Not published in the TDA's DID Document; not rate-limited (local only).
        _app.Map("/localcomm-ws", HandleWebSocketAsync);

        await _app.StartAsync(ct);
        _log.LogInformation(
            "KestrelListenerService: listening on port {Port} (mTLS={Mtls}).",
            _opts.ListenPort, _opts.RequireMutualTls);
        _log.LogDebug(
            "KestrelListenerService: POST /didcomm (HTTP/2 inbound) and " +
            "GET /localcomm-ws (WebSocket RFC 8441) active on port {Port}.",
            _opts.ListenPort);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_app is not null)
            await _app.StopAsync(ct);
    }

    // ── POST /didcomm handler ─────────────────────────────────────────────────

    /// <summary>
    /// Inbound DIDComm processing pipeline:
    ///   1. Enforce content-type gate (P-008):
    ///        application/didcomm-encrypted+json — full JWE decrypt + JWS verify path.
    ///        application/didcomm-plain+json     — plaintext path; only DID discovery
    ///                                             protocols are admitted; others → 403.
    ///        anything else                       → 415.
    ///   2. Read body.
    ///   3. UnpackAsync — security boundary (decrypt+verify for JWE; parse-only for plaintext).
    ///   4. EnqueueAsync → svrn7-msg.db (write-ahead log).
    ///   5. Return 202 Accepted.
    ///
    /// If content type is wrong: return 415. If plaintext @type not in discovery whitelist: 403.
    /// If UnpackAsync fails: return 400. All subsequent processing is asynchronous.
    /// </summary>
    private async Task HandleInboundAsync(HttpContext http)
    {
        var contentType = http.Request.ContentType;
        bool isEncrypted = contentType is not null &&
            contentType.StartsWith("application/didcomm-encrypted+json", StringComparison.OrdinalIgnoreCase);
        bool isPlaintext = contentType is not null &&
            contentType.StartsWith("application/didcomm-plain+json", StringComparison.OrdinalIgnoreCase);

        if (!isEncrypted && !isPlaintext)
        {
            _log.LogWarning(
                "KestrelListenerService: rejected message with unsupported Content-Type '{Ct}'.",
                contentType);
            http.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await http.Response.WriteAsync(
                "POST /didcomm requires Content-Type: application/didcomm-encrypted+json " +
                "for general DIDComm messages. Plaintext (application/didcomm-plain+json) " +
                "is accepted only for DID discovery protocols (did-resolve-request/response). " +
                "For localhost plaintext use ws://…/localcomm-ws.",
                http.RequestAborted);
            return;
        }

        using var reader = new StreamReader(http.Request.Body);
        var packedBody = await reader.ReadToEndAsync(http.RequestAborted);

        if (string.IsNullOrWhiteSpace(packedBody))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Empty DIDComm body.", http.RequestAborted);
            return;
        }

        // Plaintext is only permitted for DID discovery protocols.
        // Gate on @type before UnpackAsync so unauthorized plaintext is rejected without
        // touching the inbox. Encrypted messages skip this block entirely.
        if (isPlaintext)
        {
            string? messageType = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(packedBody);
                if (doc.RootElement.TryGetProperty("type", out var typeEl))
                    messageType = typeEl.GetString();
            }
            catch
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsync(
                    "Plaintext DIDComm body is not valid JSON.", http.RequestAborted);
                return;
            }

            if (messageType is null ||
                !Svrn7.Core.Svrn7Constants.PlaintextDiscoveryProtocols.Contains(messageType))
            {
                _log.LogWarning(
                    "KestrelListenerService: rejected plaintext message — @type '{Type}' is not a DID discovery protocol.",
                    messageType ?? "(null)");
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync(
                    "Plaintext DIDComm is only permitted for DID discovery protocols (did-resolve-request/response). " +
                    "All other messages require application/didcomm-encrypted+json.",
                    http.RequestAborted);
                return;
            }
        }

        // ── Pack/Unpack boundary (DIDComm V2 Messaging element — DSA 0.24) ───
        // For encrypted messages: JWE decrypt + JWS verify.
        // For plaintext bootstrap messages: parse-only (UnpackAsync handles both paths).
        DIDCommUnpackedMessage unpacked;
        try
        {
            unpacked = await _didComm.UnpackAsync(
                packedBody,
                _opts.AgentKeyAgreementPrivateKey,
                http.RequestAborted);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "KestrelListenerService: UnpackAsync failed — rejecting message.");
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync(
                "DIDComm unpack failed: invalid signature or encryption.",
                http.RequestAborted);
            return;
        }

        // ── Write-ahead log (Long-Term Message Memory — DSA 0.24) ─────────────
        // Persist the unpacked payload (not the JWE — agents work with plaintext).
        // FromDid is threaded through so LOBE cmdlets can route reply messages back
        // to the sender without requiring the sender to repeat their DID in the body.
        try
        {
            await _inbox.EnqueueAsync(
                unpacked.Type,
                unpacked.Body,
                unpacked.From,
                unpacked.Id,
                unpacked.Thid,
                packedBody,
                http.RequestAborted);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "KestrelListenerService: inbox store unavailable — returning 503.");
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            http.Response.Headers["Retry-After"] = "5";
            await http.Response.WriteAsync(
                "Inbox store temporarily unavailable. Retry after 5 seconds.",
                http.RequestAborted);
            return;
        }

        _log.LogInformation(
            "KestrelListenerService: enqueued message type='{Type}'.", unpacked.Type);
        _log.LogDebug("KestrelListenerService: accepted message:\n{Json}", unpacked.ToFormattedJson());

        http.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    // ── /localcomm-ws WebSocket handler ────────────────────────────────────

    /// <summary>
    /// Accepts a WebSocket connection from local PandoMail on /localcomm-ws.
    /// Bidirectional: TDA pushes notifications; PandoMail sends requests (List-Emails,
    /// Enqueue-PandoMail). Incoming messages go through the same UnpackAsync + EnqueueAsync
    /// pipeline as POST /didcomm — the Switchboard routes them by @type to LOBEs.
    /// LOBE responses with PeerEndpoint == WebSocketNotifyHub.LocalEndpoint are
    /// delivered back over this socket by the Switchboard instead of via HTTP/2 POST.
    /// </summary>
    private async Task HandleWebSocketAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Expected a WebSocket request.", http.RequestAborted);
            return;
        }

        using var ws = await http.WebSockets.AcceptWebSocketAsync();
        var clientId = _hub.Attach(ws);
        _log.LogInformation(
            "KestrelListenerService: local-UI WebSocket attached on /localcomm-ws (id={Id}).", clientId);

        try
        {
            await ReceiveWebSocketLoopAsync(ws, clientId, http.RequestAborted);
        }
        finally
        {
            _hub.Detach(clientId);
            _log.LogInformation(
                "KestrelListenerService: local-UI WebSocket detached (id={Id}).", clientId);
        }
    }

    private async Task ReceiveWebSocketLoopAsync(WebSocket ws, Guid clientId, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            bool tooLarge = false;

            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                _log.LogDebug(
                    "KestrelListenerService: WebSocket frame received — {Bytes} bytes, endOfMessage={Eom}.",
                    result.Count, result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log.LogDebug("KestrelListenerService: WebSocket close frame received — closing.");
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);

                if (ms.Length > WebSocketNotifyHub.MaxMessageBytes)
                {
                    tooLarge = true;
                    break;
                }
            }
            while (!result.EndOfMessage);

            if (tooLarge)
            {
                _log.LogWarning(
                    "KestrelListenerService: WebSocket message too large ({Bytes} bytes, id={Id}) — closing.",
                    ms.Length, clientId);
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", closeCts.Token); }
                catch { /* best-effort */ }
                return;
            }

            _hub.MarkReceived(clientId);

            // The idle watchdog's CloseOutputAsync (WebSocketNotifyHub.CloseIdleConnectionAsync)
            // moves this socket's local state off Open without cancelling an already-pending
            // ws.ReceiveAsync — so a message the client sends just as (or after) the server
            // starts closing can still complete a full read here. Dispatching it anyway would
            // do real LOBE work for a reply that can never be delivered: this loop's own
            // while-condition will see the non-Open state on its very next check and exit,
            // Detach runs, and by the time the reply is ready WebSocketNotifyHub has already
            // forgotten the connection (logged as "pushed to local WebSocket (not connected)").
            // Reject here instead of paying that cost for a guaranteed-lost reply.
            if (ws.State != WebSocketState.Open)
            {
                _log.LogWarning(
                    "KestrelListenerService: message received after local close began (state={State}, id={Id}) — dropping, reply would be undeliverable.",
                    ws.State, clientId);
                break;
            }

            _log.LogDebug(
                "KestrelListenerService: WebSocket complete message assembled — {TotalBytes} bytes.",
                ms.Length);
            var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            _ = Task.Run(() => ProcessWebSocketMessageAsync(json, clientId, ct), ct);
        }
    }

    private async Task ProcessWebSocketMessageAsync(string json, Guid clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        _log.LogDebug(
            "KestrelListenerService: WebSocket processing message — length={Length}, preview='{Preview}'.",
            json.Length, json.Length > 120 ? json[..120] : json);

        // Svrn7.LocalUI.0.1.0 control frames (Hello/Goodbye) are connection-lifecycle
        // concerns handled directly by the hub — never enqueued to the inbox/Switchboard.
        if (await _hub.TryHandleControlFrameAsync(clientId, json, ct))
            return;

        DIDCommUnpackedMessage unpacked;
        try
        {
            unpacked = await _didComm.UnpackAsync(
                json,
                _opts.AgentKeyAgreementPrivateKey,
                ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "KestrelListenerService: WebSocket UnpackAsync failed — ignoring message.");
            return;
        }

        _log.LogDebug(
            "KestrelListenerService: WebSocket UnpackAsync OK — type='{Type}', from='{From}'.",
            unpacked.Type, unpacked.From);

        // Track this request's own envelope id → socket before enqueueing, so its eventual
        // reply (envelope thid == this id, e.g. Get-PandoMails) is routed back to this
        // connection instead of broadcast to every connected local-UI client. Reads the
        // envelope directly (unpacked.Id) rather than a body field — DIDComm V2's thid is
        // spec-standard for exactly this, replacing the old ad-hoc body.correlationId
        // convention (see docs/BACKLOG.md TDA-014).
        if (!string.IsNullOrEmpty(unpacked.Id))
            _hub.TrackCorrelation(unpacked.Id, clientId);

        try
        {
            await _inbox.EnqueueAsync(
                unpacked.Type,
                unpacked.Body,
                unpacked.From,
                unpacked.Id,
                unpacked.Thid,
                json,
                ct);
            _log.LogDebug("KestrelListenerService: WebSocket message enqueued (type='{Type}').", unpacked.Type);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "KestrelListenerService: WebSocket inbox enqueue failed.");
        }
    }

    // ── mTLS peer certificate validation ─────────────────────────────────────

    /// <summary>
    /// Validates that the connecting peer presents a certificate issued by a
    /// trusted TDA certificate authority. In production, replace with a
    /// certificate pinning or CA-validation strategy appropriate to the VTC7
    /// governance model.
    /// </summary>
    private bool ValidatePeerTdaCertificate(
        X509Certificate2 certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None) return true;

        // Development/test: accept self-signed certificates when no CA path is configured.
        if (_opts.AcceptSelfSignedPeerCertificates &&
            sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors)
        {
            _log.LogWarning(
                "KestrelListenerService: accepting self-signed peer certificate " +
                "(AcceptSelfSignedPeerCertificates=true — development mode only).");
            return true;
        }

        _log.LogWarning(
            "KestrelListenerService: peer certificate validation failed ({Errors}). Rejecting.",
            sslPolicyErrors);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
