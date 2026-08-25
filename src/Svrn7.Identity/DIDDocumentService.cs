using System.Diagnostics;
using Svrn7.Core.Interfaces;
using Svrn7.Core.Models;

namespace Svrn7.Identity;

/// <summary>
/// Decorates an <see cref="IDidDocumentRegistry"/> with OpenTelemetry Activity tracing
/// for every DID Document lifecycle operation. Registered in place of the underlying
/// registry (e.g. LiteDidDocumentRegistry) so every consumer — resolvers, drivers, LOBE
/// cmdlets — is traced transparently without call-site changes.
/// </summary>
public sealed class DIDDocumentService : IDidDocumentRegistry
{
    public static readonly ActivitySource ActivitySource =
        new("Svrn7.Identity.DIDDocument", "0.8.0");

    private readonly IDidDocumentRegistry _registry;

    public DIDDocumentService(IDidDocumentRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Resolves a DID Document by DID. Emits a DIDDocument.Resolve activity span
    /// with found, version, and W3C error code tags.
    /// </summary>
    public async Task<DidResolutionResult> ResolveAsync(string did, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.Resolve");
        activity?.SetTag("did", did);

        var result = await _registry.ResolveAsync(did, ct);

        activity?.SetTag("found", result.Found);
        if (result.Document is not null)
            activity?.SetTag("did.version", result.Document.Version);
        if (result.ErrorCode is not null)
            activity?.SetTag("error.code", result.ErrorCode);

        return result;
    }

    /// <summary>
    /// Creates a new DID Document and emits a DIDDocument.Create activity span
    /// with DID, method, and role tags.
    /// </summary>
    public async Task CreateAsync(DidDocument document, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.Create");
        activity?.SetTag("did", document.Did);
        activity?.SetTag("did.method", document.MethodName);
        activity?.SetTag("did.role", document.Role?.ToString() ?? "none");
        activity?.SetTag("did.version", document.Version);

        await _registry.CreateAsync(document, ct);
    }

    /// <summary>
    /// Updates an existing DID Document (version must be current+1) and emits a
    /// DIDDocument.Update activity span with DID and new version tags.
    /// </summary>
    public async Task UpdateAsync(DidDocument document, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.Update");
        activity?.SetTag("did", document.Did);
        activity?.SetTag("did.version.new", document.Version);

        await _registry.UpdateAsync(document, ct);
    }

    /// <summary>
    /// Permanently deactivates a DID Document and emits a DIDDocument.Deactivate
    /// activity span. Deactivation is irreversible.
    /// </summary>
    public async Task DeactivateAsync(string did, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.Deactivate");
        activity?.SetTag("did", did);

        await _registry.DeactivateAsync(did, ct);
    }

    /// <summary>
    /// Suspends a DID Document and emits a DIDDocument.Suspend activity span.
    /// </summary>
    public async Task SuspendAsync(string did, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.Suspend");
        activity?.SetTag("did", did);

        await _registry.SuspendAsync(did, ct);
    }

    /// <summary>
    /// Reinstates a previously suspended DID Document and emits a
    /// DIDDocument.Reinstate activity span.
    /// </summary>
    public async Task ReinstateAsync(string did, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.Reinstate");
        activity?.SetTag("did", did);

        await _registry.ReinstateAsync(did, ct);
    }

    /// <summary>
    /// Retrieves the full version history for a DID and emits a DIDDocument.GetHistory
    /// activity span with the total version count.
    /// </summary>
    public async Task<IReadOnlyList<DidDocument>> GetHistoryAsync(
        string did, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.GetHistory");
        activity?.SetTag("did", did);

        var history = await _registry.GetHistoryAsync(did, ct);

        activity?.SetTag("version.count", history.Count);
        return history;
    }

    /// <summary>
    /// Retrieves a specific version snapshot of a DID Document and emits a
    /// DIDDocument.ResolveVersion activity span.
    /// </summary>
    public async Task<DidDocument?> ResolveVersionAsync(
        string did, int version, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.ResolveVersion");
        activity?.SetTag("did", did);
        activity?.SetTag("did.version", version);

        var doc = await _registry.ResolveVersionAsync(did, version, ct);

        activity?.SetTag("found", doc is not null);
        return doc;
    }

    /// <summary>
    /// Reports whether a DID currently resolves to an Active document and emits
    /// a DIDDocument.IsActive activity span.
    /// </summary>
    public async Task<bool> IsActiveAsync(string did, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.IsActive");
        activity?.SetTag("did", did);

        var active = await _registry.IsActiveAsync(did, ct);

        activity?.SetTag("active", active);
        return active;
    }

    /// <summary>
    /// Finds the DID owning a given public key and emits a
    /// DIDDocument.FindByPublicKey activity span.
    /// </summary>
    public async Task<string?> FindDidByPublicKeyHexAsync(string publicKeyHex, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.FindByPublicKey");

        var did = await _registry.FindDidByPublicKeyHexAsync(publicKeyHex, ct);

        activity?.SetTag("found", did is not null);
        if (did is not null) activity?.SetTag("did", did);
        return did;
    }

    /// <summary>
    /// Queries DID Documents by method and/or status and emits a DIDDocument.Query
    /// activity span with the result count.
    /// </summary>
    public async Task<IReadOnlyList<DidDocument>> QueryAsync(
        string? methodName = null, DidStatus? status = null, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.Query");
        activity?.SetTag("did.method", methodName ?? "(any)");
        activity?.SetTag("did.status", status?.ToString() ?? "(any)");

        var results = await _registry.QueryAsync(methodName, status, ct);

        activity?.SetTag("result.count", results.Count);
        return results;
    }

    /// <summary>
    /// Returns the total number of registered DID Documents and emits a
    /// DIDDocument.Count activity span.
    /// </summary>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("DIDDocument.Count");

        var count = await _registry.CountAsync(ct);

        activity?.SetTag("count", count);
        return count;
    }

    /// <summary>
    /// Returns a one-line diagnostic summary of a DidDocument for log output.
    /// Null-safe — returns "(not found)" when the document is null.
    /// </summary>
    public static string Summarize(DidDocument? doc) =>
        doc is null
            ? "(not found)"
            : $"DID={doc.Did} Version={doc.Version} Status={doc.Status} Role={doc.Role} " +
              $"Keys={doc.VerificationMethod.Count} Services={doc.ServiceEndpoints.Count} " +
              $"UpdatedAt={doc.UpdatedAt:O}";
}
