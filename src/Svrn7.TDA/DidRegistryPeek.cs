using System.Diagnostics;
using LiteDB;

namespace Svrn7.TDA;

/// <summary>
/// Reads a DIDComm service endpoint out of an instance's <b>encrypted</b>
/// <c>svrn7-dids.db</c> in the pre-host block — before the Generic Host (and the
/// DI-registered <c>DidRegistryLiteContext</c>) exist. This is the single secure
/// source for endpoint URLs: the cleartext <c>identity.meta.json</c> deliberately
/// carries none, so a local attacker cannot redirect a TDA's listener or its
/// DID-resolution escalation by editing a plaintext file
/// (docs/AGENTWALLET.md §6, SECURITY.md §11.3).
///
/// Opens the database read-only in a tight scope and disposes immediately, so the
/// DI context can take its exclusive lock a moment later during host start.
/// </summary>
internal static class DidRegistryPeek
{
    private const string DocumentsCollection = "Documents";

    /// <summary>
    /// The <c>DIDCommMessaging</c> (or first <c>…/didcomm</c>) service endpoint URL
    /// for <paramref name="did"/>, or null if the document or an endpoint is absent.
    /// </summary>
    public static string? TryReadServiceEndpoint(string didsDbPath, string dbPasswordHex, string did) =>
        TryReadServiceEndpoint(didsDbPath, dbPasswordHex, did, out _);

    /// <summary>
    /// As above, and reports a low-cardinality <paramref name="outcome"/>
    /// (<see cref="BootstrapDiagnostics.Peek"/>) for telemetry — the caller records
    /// the metric once the host meter pipeline is live. Emits a
    /// <c>tda.bootstrap.peek_endpoint</c> span whenever a listener is attached.
    /// </summary>
    public static string? TryReadServiceEndpoint(
        string didsDbPath, string dbPasswordHex, string did, out string outcome)
    {
        using var activity = BootstrapDiagnostics.ActivitySource.StartActivity(
            BootstrapDiagnostics.ActivityPeekEndpoint);

        outcome = BootstrapDiagnostics.Peek.Error;
        try
        {
            if (!File.Exists(didsDbPath))
            {
                outcome = BootstrapDiagnostics.Peek.NoFile;
                return null;
            }

            using var db = new LiteDatabase(
                $"Filename=\"{didsDbPath}\";Password={dbPasswordHex};ReadOnly=true");

            var doc = db.GetCollection(DocumentsCollection).FindOne(Query.EQ("Did", did));
            if (doc is null)
            {
                outcome = BootstrapDiagnostics.Peek.NoDocument;
                return null;
            }

            if (!doc.TryGetValue("ServiceEndpoints", out var svcs) || !svcs.IsArray)
            {
                outcome = BootstrapDiagnostics.Peek.NoEndpoint;
                return null;
            }

            BsonValue? chosen = null;
            foreach (var s in svcs.AsArray)
            {
                if (!s.IsDocument) continue;
                var ep = s.AsDocument.TryGetValue("ServiceEndpoint", out var v) && v.IsString ? v.AsString : null;
                if (ep is null) continue;
                var type = s.AsDocument.TryGetValue("Type", out var t) && t.IsString ? t.AsString : "";

                if (type.Equals("DIDCommMessaging", StringComparison.OrdinalIgnoreCase)
                    || ep.EndsWith("/didcomm", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = ep;
                    break;
                }
                chosen ??= ep;
            }

            outcome = chosen is null ? BootstrapDiagnostics.Peek.NoEndpoint : BootstrapDiagnostics.Peek.Found;
            return chosen?.AsString;
        }
        catch
        {
            outcome = BootstrapDiagnostics.Peek.Error;
            return null; // corrupt / wrong key / locked — caller falls back to its error path
        }
        finally
        {
            activity?.SetTag(BootstrapDiagnostics.TagOutcome, outcome);
        }
    }

    /// <summary>The TCP port from a <c>http(s)://host:port/path</c> endpoint URL, or null.</summary>
    public static int? PortOf(string? endpointUrl) =>
        Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : null;

    /// <summary>The scheme+host (no port, no path) — the <c>--url</c> equivalent — from an endpoint URL, or null.</summary>
    public static string? BaseUrlOf(string? endpointUrl) =>
        Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}" : null;
}
