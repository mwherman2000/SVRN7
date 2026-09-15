using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Svrn7.Core;

namespace Svrn7.TDA;

// ── IsolatedRunspaceFactory ───────────────────────────────────────────────────────
//
// Derived from: "PowerShell Runspace Pool" (Runspace Pool element type) — DSA 0.24 Epoch 0.
//
// Builds the shared InitialSessionState (ISS) via LobeManager and vends
// per-invocation IsolatedPipeline instances.  Each dispatch creates its own
// Runspace from the ISS — a crash or runaway cmdlet in one pipeline cannot
// affect any other concurrent dispatch.
// Owns the 60-second epoch refresh timer that keeps Svrn7RunspaceContext.CurrentEpoch
// current without per-runspace polling.

/// <summary>
/// Singleton that builds the shared <see cref="InitialSessionState"/> and vends
/// per-invocation <see cref="IsolatedPipeline"/> instances for LOBE dispatch.
/// Derived from: PowerShell Runspace Pool — DSA 0.24 Epoch 0 (PPML).
/// </summary>
public sealed class IsolatedRunspaceFactory : IDisposable
{
    private readonly LobeManager             _lobes;
    private readonly Svrn7RunspaceContext    _ctx;
    private readonly ILogger<IsolatedRunspaceFactory> _log;

    private InitialSessionState? _iss;
    private Timer?               _epochTimer;
    private bool                 _disposed;

    public IsolatedRunspaceFactory(
        IOptions<TdaOptions>         opts,
        LobeManager                  lobes,
        Svrn7RunspaceContext         ctx,
        ILogger<IsolatedRunspaceFactory> log)
    {
        _lobes = lobes;
        _ctx   = ctx;
        _log   = log;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="InitialSessionState"/> via <see cref="LobeManager"/>
    /// and starts the epoch refresh timer.
    /// Called once by <see cref="SwitchboardHostedService"/> on startup.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _iss = _lobes.BuildInitialSessionState();
        _log.LogInformation("IsolatedRunspaceFactory: InitialSessionState built — per-invocation runspace isolation active.");

        // 60-second epoch refresh — keeps $SVRN7.CurrentEpoch current in all runspaces.
        _epochTimer = new Timer(
            _ => RefreshEpoch(),
            state:     null,
            dueTime:   TimeSpan.Zero,
            period:    TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Creates an isolated <see cref="PowerShell"/> pipeline backed by a dedicated
    /// <see cref="Runspace"/> opened from the shared <see cref="InitialSessionState"/>.
    /// Each LOBE invocation gets its own runspace — a crash or runaway cmdlet cannot
    /// affect other concurrent dispatches. Caller must dispose the returned instance.
    /// </summary>
    public IsolatedPipeline CreateIsolatedPipeline()
    {
        if (_iss is null)
            throw new InvalidOperationException(
                "IsolatedRunspaceFactory has not been started. Call Start() first.");
        return new IsolatedPipeline(_iss, _log);
    }

    // ── Epoch refresh ─────────────────────────────────────────────────────────

    private void RefreshEpoch()
    {
        // In a full implementation this would query ISvrn7Driver.GetCurrentEpochAsync().
        // For v0.8.0 the epoch is always Endowment (0); the infrastructure is in place
        // for Epoch 1 advancement without code changes.
        var epoch = Svrn7.Core.Svrn7Constants.Epochs.Endowment;
        _ctx.SetEpoch(epoch);
        _log.LogDebug("IsolatedRunspaceFactory: epoch refreshed to {Epoch}.", epoch);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _epochTimer?.Dispose();
        _log.LogInformation("IsolatedRunspaceFactory: disposed.");
    }
}

// ── IsolatedPipeline ──────────────────────────────────────────────────────────

/// <summary>
/// Pairs a <see cref="PowerShell"/> instance with its own dedicated
/// <see cref="Runspace"/> opened from the shared <see cref="InitialSessionState"/>.
/// Disposing closes and releases both. A fault in one <see cref="IsolatedPipeline"/>
/// cannot affect any other concurrent dispatch.
/// </summary>
public sealed class IsolatedPipeline : IDisposable
{
    /// <summary>The PowerShell pipeline bound to the dedicated runspace.</summary>
    public PowerShell Ps { get; }

    private readonly Runspace _runspace;
    private readonly Activity? _lifetimeActivity;
    private bool              _disposed;

    /// <summary>
    /// Spans this instance's full lifetime — started here at creation, stopped in
    /// Dispose() — rather than a single bounded operation like the other
    /// Svrn7Telemetry activities. Its duration is exactly how long this dispatch held
    /// its own isolated runspace, the same metric that would surface a leak (a
    /// pipeline created but never disposed) or the JIT-LOBE reimport overhead
    /// (docs/BACKLOG.md TDA-001a) in a trace backend.
    /// </summary>
    internal IsolatedPipeline(InitialSessionState iss, ILogger? log = null)
    {
        _runspace = RunspaceFactory.CreateRunspace(iss);
        _lifetimeActivity = Svrn7Telemetry.Source.StartActivity(
            Svrn7Telemetry.ActivityRunspace, ActivityKind.Internal);
        _lifetimeActivity?.SetTag(Svrn7Telemetry.TagRunspaceId, _runspace.InstanceId.ToString());
        try
        {
            _runspace.Open();
            Ps = PowerShell.Create();
            Ps.Runspace = _runspace;
            // ProbeRunspace() runs a second script invocation on the new runspace purely
            // for diagnostics — guard it behind IsEnabled so that cost (31-140ms, measured
            // live) isn't paid on every dispatch when Debug logging is off (the default —
            // IsolatedRunspaceFactory logs at Information in appsettings.json). Passing it
            // as a LogDebug argument alone doesn't skip the call: C# evaluates method
            // arguments before the callee's own level check runs.
            if (log is not null && log.IsEnabled(LogLevel.Debug))
                log.LogDebug("IsolatedPipeline: {Probe}", ProbeRunspace());
            _lifetimeActivity?.SetTag(Svrn7Telemetry.TagOutcome, "open");
        }
        catch (Exception ex)
        {
            _lifetimeActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _lifetimeActivity?.Dispose();
            throw;
        }
    }

    private string ProbeRunspace()
    {
        using var p = PowerShell.Create();
        p.Runspace = _runspace;
        p.AddScript(
            "$mods = (Get-Module).Name -join ', '; " +
            "$dir  = if ($SVRN7_LOBES_DIR) { $SVRN7_LOBES_DIR } else { '<empty>' }; " +
            "$cmd  = [bool](Get-Command Invoke-Svrn7IncomingTransfer -ErrorAction SilentlyContinue); " +
            "\"modules=[$mods]  LOBES_DIR=[$dir]  hasInvokeTransfer=$cmd\"");
        var result = p.Invoke().FirstOrDefault()?.ToString() ?? "(probe failed — no output)";
        if (p.HadErrors)
            result += "  PROBE_ERRORS=[" +
                      string.Join("; ", p.Streams.Error.Select(e => e.ToString())) + "]";
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Ps.Dispose(); }         catch { /* best-effort */ }
        try { _runspace.Close(); }    catch { /* best-effort */ }
        try { _runspace.Dispose(); }  catch { /* best-effort */ }

        // Stops the span — its recorded duration is this instance's full lifetime,
        // creation through disposal.
        _lifetimeActivity?.SetTag(Svrn7Telemetry.TagOutcome, "disposed")
                          .SetStatus(ActivityStatusCode.Ok);
        _lifetimeActivity?.Dispose();
    }
}
