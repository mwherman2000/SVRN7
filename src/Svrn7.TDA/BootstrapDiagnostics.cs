using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Svrn7.TDA;

/// <summary>
/// Instrumentation for the pre-host bootstrap path — instance discovery, the
/// encrypted-DID-registry endpoint peek (<see cref="DidRegistryPeek"/>), listen-port
/// resolution, the unconditional <c>identity.meta.json</c> rewrite, and the
/// post-<c>Build()</c> parent-tier endpoint resolution.
///
/// One <see cref="ActivitySource"/> and one <see cref="Meter"/>, both named
/// <c>Svrn7.TDA.Bootstrap</c>. Nothing here exports anything: with no listener the
/// calls are no-ops. The TDA host opts in via <c>AddSource</c> / <c>AddMeter</c>
/// in <c>Program.cs</c>.
///
/// Attribute hygiene (same rule as <c>AgentWalletDiagnostics</c>): tag values are
/// only ever the low-cardinality outcome strings below plus the listen port — never
/// a DID, a file path, a URL host, a key, or any per-identity identifier.
/// </summary>
internal static class BootstrapDiagnostics
{
    public const string SourceName = "Svrn7.TDA.Bootstrap";

    private static readonly string AssemblyVersion =
        typeof(BootstrapDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public static readonly ActivitySource ActivitySource = new(SourceName, AssemblyVersion);
    public static readonly Meter Meter = new(SourceName, AssemblyVersion);

    // ── Activity names ───────────────────────────────────────────────────────
    public const string ActivityBootstrap     = "tda.bootstrap";
    public const string ActivityPeekEndpoint  = "tda.bootstrap.peek_endpoint";
    public const string ActivityResolveParent = "tda.bootstrap.resolve_parent_endpoint";

    // ── Tag keys — the ONLY strings usable as tag names ──────────────────────
    public const string TagOutcome     = "svrn7.outcome";
    public const string TagPortSource  = "svrn7.port_source";
    public const string TagPhase       = "svrn7.phase";
    public const string TagListenPort  = "svrn7.listen_port";
    public const string TagFirstRun    = "svrn7.first_run";
    public const string TagRepublish   = "svrn7.republish_endpoint";
    public const string TagPersisted   = "svrn7.persisted";

    // ── Instruments ─────────────────────────────────────────────────────────
    public static readonly Counter<long> EndpointPeek = Meter.CreateCounter<long>(
        "tda.bootstrap.endpoint_peek", unit: "{peek}",
        description: "Reads of this identity's DIDComm endpoint from the encrypted svrn7-dids.db at startup, tagged by outcome (found, no_file, no_document, no_endpoint, error).");

    public static readonly Counter<long> PortResolved = Meter.CreateCounter<long>(
        "tda.bootstrap.port_resolved", unit: "{resolution}",
        description: "Listen-port resolutions at startup, tagged by source (first_run_explicit, first_run_auto, did_document, republish).");

    public static readonly Counter<long> MetaWrite = Meter.CreateCounter<long>(
        "tda.bootstrap.meta_write", unit: "{write}",
        description: "identity.meta.json writes, tagged by phase (first_run, restart).");

    public static readonly Counter<long> ParentEndpointResolve = Meter.CreateCounter<long>(
        "tda.bootstrap.parent_endpoint_resolve", unit: "{resolution}",
        description: "Parent-tier (Society/Federation) endpoint resolutions from the parent's DID Document, tagged by outcome (resolved, no_parent, unresolvable).");

    // ── Outcome constants ───────────────────────────────────────────────────
    public static class Peek
    {
        public const string Found      = "found";
        public const string NoFile     = "no_file";
        public const string NoDocument = "no_document";
        public const string NoEndpoint = "no_endpoint";
        public const string Error      = "error";
    }

    public static class PortSource
    {
        public const string FirstRunExplicit = "first_run_explicit";
        public const string FirstRunAuto     = "first_run_auto";
        public const string DidDocument      = "did_document";
        public const string Republish        = "republish";
    }

    public static class ParentOutcome
    {
        public const string Resolved     = "resolved";
        public const string NoParent     = "no_parent";
        public const string Unresolvable = "unresolvable";
    }

    // ── Helpers ────────────────────────────────────────────────────────────
    public static void RecordEndpointPeek(string outcome) =>
        EndpointPeek.Add(1, new KeyValuePair<string, object?>(TagOutcome, outcome));

    public static void RecordPortResolved(string source) =>
        PortResolved.Add(1, new KeyValuePair<string, object?>(TagPortSource, source));

    public static void RecordMetaWrite(bool firstRun) =>
        MetaWrite.Add(1, new KeyValuePair<string, object?>(TagPhase, firstRun ? "first_run" : "restart"));

    public static void RecordParentEndpointResolve(string outcome) =>
        ParentEndpointResolve.Add(1, new KeyValuePair<string, object?>(TagOutcome, outcome));
}
