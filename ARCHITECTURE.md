# Web 7.0 Pando — Architecture Notes

This file collects design-rationale documentation for TDA runtime decisions that
don't fit inline as code comments. See [README.md](./README.md) for the full
system overview and `CLAUDE.md` for AI-assistant project context.

---

## Design Rationale: Execution Policy Scoping (IsolatedRunspaceFactory / LobeManager)

### Problem

LOBE modules (`.psm1` files under `lobes/`) are unsigned PowerShell scripts. On a
host machine where the local execution policy is `Restricted` or `AllSigned`,
`Import-Module` on a LOBE fails with:

```
File ...\lobes\Svrn7.Common.0.8.0\Svrn7.Common.0.8.0.psm1 cannot be loaded because
running scripts is disabled on this system.
System.Management.Automation.PSSecurityException
```

This surfaces as a `DIDCommMessageSwitchboard` dispatch failure — every inbound
message routed to a LOBE fails until the module can be imported.

### Fix

`LobeManager.BuildInitialSessionState()` sets:

```csharp
var iss = InitialSessionState.CreateDefault2();
AddBuiltInCmdlets(iss);

// Scoped to these isolated runspaces only — independent of the host machine's
// Set-ExecutionPolicy, which otherwise blocks Import-Module on unsigned LOBE .psm1 files.
iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
```

`InitialSessionState.ExecutionPolicy` is a property of the session state object
itself, independent of the machine-wide (`LocalMachine`) and user-wide
(`CurrentUser`) `Set-ExecutionPolicy` scopes. Setting it here scopes `Bypass` to
only the runspaces `IsolatedRunspaceFactory` opens for LOBE dispatch — it does
not change policy for any other PowerShell session on the host (interactive
shells, other applications, CI).

### Why not fix this via `Set-ExecutionPolicy` on the host?

- Would require every deployment target (dev machines, CI runners, production
  hosts) to carry an out-of-band machine configuration step, undocumented in
  source control.
- Would weaken execution policy for *all* PowerShell activity on that machine,
  not just the TDA's LOBE runspaces — broader blast radius than necessary.
- The TDA already treats LOBEs as first-party, descriptor-registered code
  (`*.lobe.json` scan, `FileSystemWatcher` hot-reload) — it is the LOBE catalog,
  not the OS execution policy, that is the real trust boundary gating which
  `.psm1` files get loaded. `Bypass` on the ISS reflects that the point of
  control already lives elsewhere.

### Why not sign the LOBE `.psm1` files?

Signing would satisfy `AllSigned`/`RemoteSigned` policies without touching
`ExecutionPolicy`, but it adds a code-signing certificate and signing step to
every LOBE authoring and hot-reload cycle — in tension with the JIT hot-update
design (`Import-Module -Force` on every dispatch, TDA-001a) where LOBEs are
meant to be dropped into `lobes/` and picked up immediately by
`FileSystemWatcher`. Scoping `ExecutionPolicy` on the ISS keeps that authoring
loop friction-free without touching host-wide policy.
