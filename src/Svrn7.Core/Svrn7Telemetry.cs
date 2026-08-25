using System.Diagnostics;

namespace Svrn7.Core;

/// <summary>
/// Central <see cref="ActivitySource"/> for SVRN7 distributed tracing — shared by every
/// layer of the DIDComm pipeline (Svrn7.TDA, Svrn7.Society) so a single instrumentation
/// scope covers receipt, storage, dispatch, LOBE import/invocation, and delivery.
/// Compatible with OpenTelemetry — attach any OTEL exporter by subscribing to
/// the <c>Svrn7.TDA</c> source name. Calls are zero-cost when no listener is registered.
/// Lives in Svrn7.Core (zero outbound dependencies) because <see cref="ActivitySource"/>
/// is part of the .NET 8 shared framework — no NuGet package required.
/// </summary>
public static class Svrn7Telemetry
{
    public const string SourceName    = "Svrn7.TDA";
    public const string SourceVersion = "1.0.0";

    public static readonly ActivitySource Source =
        new(SourceName, SourceVersion);

    // ── Activity names ────────────────────────────────────────────────────────
    public const string ActivityReceive   = "didcomm.receive";
    public const string ActivityDispatch  = "didcomm.dispatch";
    public const string ActivityInvoke    = "didcomm.invoke";
    public const string ActivityDeliver   = "didcomm.deliver";
    public const string ActivityImport    = "lobe.import";
    public const string ActivityStorage   = "didcomm.storage";

    // ── Tag names (follow OpenTelemetry messaging/db semconv where possible) ──
    public const string TagMessageId      = "messaging.message_id";
    public const string TagMessageType    = "messaging.message_type";
    public const string TagAttemptCount   = "messaging.attempt_count";
    public const string TagLobeName       = "svrn7.lobe_name";
    public const string TagLobeEntrypoint = "svrn7.lobe_entrypoint";
    public const string TagLobeModulePath = "svrn7.lobe_module_path";
    public const string TagLobeKind       = "svrn7.lobe_kind";       // eager | jit
    public const string TagOutcome        = "svrn7.outcome";
    public const string TagPeerEndpoint   = "svrn7.peer_endpoint";
    public const string TagTransport      = "svrn7.transport";
    public const string TagContentType    = "svrn7.content_type";
    public const string TagDbOperation    = "db.operation";
    public const string TagRecordCount    = "svrn7.record_count";
    public const string TagResultCount    = "svrn7.result_count";
    public const string TagWarningCount   = "svrn7.warning_count";
}
