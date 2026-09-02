# SVRN7 TDA — Backlog

---

## TDA-007 — Rationalize protocol URI naming and versioning around LOBE names ✓ DONE

**Area:** all `.lobe.json` descriptors, agent scripts, DIDComm integration guide, BACKLOG TDA-006

**Summary:** Protocol URI path segments and version numbers must be derived
directly from LOBE names and LOBE versions — no independent invention.  The
LOBE name is the source of truth.  This also makes TDA-006 (on-demand LOBE
download) trivial: the NuGet package ID is read directly from the URI with no
transformation.

---

### Naming and version convention (to be enforced)

**Rule:** `did:drn:svrn7.net/protocols/{LobeName}.{lobe.version}/{action}`

- `{LobeName}` — the full LOBE name exactly as it appears in `lobe.name`
  (e.g. `Svrn7.Email`, `Pando.Diagnostics`).  Case-preserved.
- `{lobe.version}` — the full three-part version from `lobe.version` (e.g. `0.8.0`).
- `{action}` — the message action name (e.g. `message`, `register-citizen`).

**Examples under the new convention:**

```
Svrn7.Email 0.8.0      did:drn:svrn7.net/protocols/PandoMail.0.8.0/message
Svrn7.Email 0.8.0      did:drn:svrn7.net/protocols/PandoMail.0.8.0/receipt
Svrn7.Federation 0.8.0 did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/register-society
Svrn7.Onboarding 0.8.0 did:drn:svrn7.net/protocols/Svrn7.Onboarding.0.8.0/register-citizen
Svrn7.Invoicing 0.8.0  did:drn:svrn7.net/protocols/Svrn7.Invoicing.0.8.0/request
Pando.Diagnostics 0.1.0 did:drn:svrn7.net/protocols/Pando.Diagnostics.0.1.0/date-query
```

**Derivation (bidirectional — no algorithm needed):**

| Direction | Rule |
|---|---|
| URI → LOBE name | second path segment after `/protocols/` is the LOBE name verbatim |
| URI → version constraint | third path segment is `lobe.version` verbatim |
| LOBE name → URI segment | use `lobe.name` from `.lobe.json` verbatim |
| LOBE version → URI version | use `lobe.version` from `.lobe.json` verbatim |

This makes TDA-006 trivial: given
`did:drn:svrn7.net/protocols/Svrn7.Onboarding.0.8.0/register-citizen`, the
NuGet package ID is `Svrn7.Onboarding` and the minimum version is `0.8.*`
— read directly from the URI, no registry lookup for the package name.

---

### Current state — all URIs must be updated

Every existing protocol URI must be renamed.  Current URIs use ad-hoc lowercase
suffixes (`email`, `federation`, `onboard`, `invoice`) and a hardcoded `1.0`
version.  The full before/after for every LOBE:

| LOBE | Current URI segment / version | New segment / version |
|---|---|---|
| `Svrn7.Calendar` | `calendar/1.0` | `Svrn7.Calendar/0.8` |
| `Svrn7.Email` | `email/1.0` | `Svrn7.Email/0.8` |
| `Svrn7.Federation` | `federation/1.0` | `Svrn7.Federation/0.8` |
| `Svrn7.Identity` | `did/1.0`, `vc/1.0` | `Svrn7.Identity/0.8` (see split note) |
| `Svrn7.Invoicing` | `invoice/1.0` | `Svrn7.Invoicing/0.8` |
| `Svrn7.Notifications` | `notification/1.0` | `Svrn7.Notifications/0.8` |
| `Svrn7.Onboarding` | `onboard/1.0` | `Svrn7.Onboarding/0.8` |
| `Svrn7.Presence` | `presence/1.0` | `Svrn7.Presence/0.8` |
| `Svrn7.Society` | `society/1.0`, `transfer/1.0` | `Svrn7.Society/0.8` (see split note) |
| `Svrn7.UX` | `ux/1.0` | `Svrn7.UX/0.8` |
| `Pando.Diagnostics` | `diagnostics/1.0` | `Pando.Diagnostics/0.1` |

`Svrn7.Common` has no protocols — no change needed.

---

### Required LOBE splits

Two LOBEs currently own protocols that belong in separate LOBEs.  A LOBE must
own exactly one URI segment (its own name):

- **`Svrn7.Identity`** owns `did/1.0/*` and `Svrn7.Identity/0.8.0/vc-*`.  These are unrelated
  concerns.  Options: (A) consolidate all under `Svrn7.Identity/0.8.0/*` and
  rename actions accordingly; (B) split into `Svrn7.DID` and `Svrn7.VC`
  (separate LOBEs, separate packages).  Decision required.

- **`Svrn7.Society`** owns `transfer/1.0/*` in addition to `society/1.0/*`.
  Transfer protocols must move to a new `Svrn7.Transfer` LOBE and be renamed
  `did:drn:svrn7.net/protocols/Svrn7.Transfer.0.8.0/*`.

---

### Version bump rules

| Change type | Protocol URI version | LOBE package version |
|---|---|---|
| Patch fix (no message format change) | `0.8.0` → `0.8.1` | `0.8.0` → `0.8.1` |
| New optional field added | `0.8.0` → `0.9.0` | `0.8.0` → `0.9.0` |
| Breaking field rename / removal | `0.8.0` → `0.9.0` or `1.0.0` | `0.8.0` → `0.9.0` or `1.0.0` |

A protocol version bump always requires a new URI.  Old and new URIs may be
registered simultaneously during a migration window (see versioning backlog).

---

### Scope of change

This is a **breaking change** across all `.lobe.json` files, all agent scripts
(`lobes/Agent*.ps1`), all integration test message fixtures, and any external
sender that has hardcoded the current URI strings.  All must be updated in a
single coordinated commit.

**No code change required in `LobeManager` or `DIDCommMessageSwitchboard`** —
the registry is URI-keyed and is indifferent to the naming convention.

**Dependencies:** must be completed before TDA-006 to make package-ID
derivation trivial.

---

## TDA-006 — On-demand LOBE download when an unknown message type arrives

**Area:** `DIDCommMessageSwitchboard`, `LobeManager`, `TdaOptions`, LOBE registry/marketplace

**Summary:** When a TDA receives a DIDComm message whose `@type` URI has no
registered handler, the Switchboard calls `MarkFailedAsync(retry: false)` —
the message is dead-lettered immediately.  A future capability would allow the
TDA to intercept that path, automatically resolve, download, and install the
required LOBE from a registry, then re-enqueue the message — making the LOBE
set self-healing and removing the need for pre-deployment configuration of
every message type a TDA will ever encounter.

**What would be required:**

1. **LOBE registry / index** — Once TDA-007 naming is in place, the NuGet
   package ID is read directly from the URI: the second path segment after
   `/protocols/` is the package ID verbatim (e.g.
   `did:drn:svrn7.net/protocols/PandoMail.0.8.0/message` → package
   `Svrn7.Email`).  The registry is still needed for one thing: the NuGet feed
   URL (`https://packages.svrn7.net/v3/index.json`).  The minimum version
   constraint is also read directly from the URI (`0.8` → `>= 0.8.0`).

   **The registry is another TDA with a specific Role.**  It is not a
   traditional HTTP service — it is a TDA whose Role includes serving LOBE
   package metadata and feed URLs via DIDComm.  `TdaOptions.LobeRegistryDid`
   holds the DID of the registry TDA (e.g. `did:drn:registry.svrn7.net`).
   The feed URL is fetched from the registry TDA via a DIDComm request rather
   than being hardcoded.

2. **Switchboard — "no handler" intercept** — `DIDCommMessageSwitchboard` must
   intercept the `reg is null` branch before calling `MarkFailedAsync` and call
   into `LobeManager.TryResolveAndInstallAsync(messageType)`.  On success the
   message is re-enqueued to the inbox for a second dispatch attempt; on failure
   (download error, timeout, policy rejection) it falls through to
   `MarkFailedAsync(retry: false)` as today.

3. **LobeManager — `TryResolveAndInstallAsync`** — New method:
   - Query the registry index for a package ID matching the protocol URI prefix.
   - Call `dotnet nuget download` (or use `HttpClient` directly against the
     NuGet v3 API) to fetch the `.nupkg` to a temp path.
   - Validate the package (reuse `Test-LOBEPackage` logic or a C# equivalent).
   - Extract to the lobes directory (reuse `Install-LOBEPackage` extraction
     logic).
   - The FileSystemWatcher picks up the new `.lobe.json` and calls
     `RegisterFromDescriptor` — this is already implemented.
   - Return success/failure to the Switchboard.

4. **Trust / signature verification** — Downloaded LOBEs should be signed and
   the signature verified before installation.  The signing key for each
   package should be pinned in `TdaOptions` or fetched from the registry
   alongside the package.  Without this, on-demand download is a remote code
   execution surface.

5. **Policy gate** — `TdaOptions.AutoInstallLobes` (bool, default `false`).
   Auto-download should be opt-in.  When disabled, the "no handler" path
   continues to drop with a warning.  When enabled, auto-install is gated by
   an allowed-list (`TdaOptions.AllowedLobeAuthors` or a signed registry
   manifest).

6. **Retry queue** — The message that triggered the download cannot be
   re-processed until the LOBE is installed and `RegisterFromDescriptor` has
   run.  A simple approach: re-enqueue the raw message bytes to the TDA inbox
   after a configurable delay (`TdaOptions.AutoInstallRetryDelayMs`).  Requires
   the inbox to tolerate duplicate delivery (idempotent handlers).

7. **Per-instance lobes directory** — This feature is only safe if each TDA
   instance has its own lobes directory (see TDA-004).  Downloading a LOBE into
   a shared directory while another instance is running can cause partial reads.

**Dependencies:** TDA-007 (naming rationalization, required for algorithmic
package-ID derivation), TDA-004 (per-instance lobes dir), LOBE registry design
(not yet started).

**No code change required now** — tracked here for design continuity.

---

## TDA-005 — TDA-to-TDA transport without HTTPS/TLS (FYI / Future Design)

**Area:** `KestrelListenerService`, `TdaOptions`, deployment

**Summary:** TDAs will normally communicate over cleartext HTTP/2 (h2c), not
HTTPS/TLS.  TLS termination, if required, will be handled by the network
infrastructure layer (load balancer, reverse proxy, service mesh) rather than
by the TDA process itself.

**Implications:**
- `TdaOptions.RequireMutualTls` default should eventually change to `false`.
- `TdaOptions.TlsCertificatePath` / `TlsCertificatePassword` become optional
  infrastructure concerns, not TDA concerns.
- The Kestrel listener should default to h2c (cleartext HTTP/2) without requiring
  a certificate to be configured.
- `AcceptSelfSignedPeerCertificates` becomes irrelevant in the h2c path.
- The outbound `HttpClient` ("didcomm") already uses `RequestVersionExact` for
  HTTP/2 and works over h2c today — no change needed there.

**Current behaviour:** When no TLS certificate is configured, `KestrelListenerService`
logs a warning and runs in cleartext HTTP/2 mode:

```
warn: Svrn7.TDA.KestrelListenerService[0]
      KestrelListenerService: TLS certificate not configured.
      Running in cleartext HTTP/2 (development mode only).
```

This warning message itself will need updating once h2c becomes the intended
production mode rather than a development fallback.

**No code change required now** — tracked here for design continuity.

---

## TDA-004 — Per-instance LOBE directory (LOBE marketplace / registry)

**Area:** `LobeManager`, `TdaOptions`, deployment, `Program.cs`

**Summary:** Today all TDA instances share a single `lobes/` folder copied at build
time.  This is sufficient while LOBEs are static and bundled with the binary.

Once a LOBE marketplace or registry exists, each TDA instance will need its own
isolated lobes folder so that LOBEs can be downloaded, installed, updated, or removed
per-instance without affecting other running TDAs.

**Current behaviour (Epoch 0):**
- LOBEs are copied to `bin/.../lobes/` at build time — the build has no knowledge of
  the runtime port.
- All instances share that folder; per-instance isolation is achieved only for
  databases (`{port}/mem/`).
- A specific instance can point at a different LOBE set today by overriding
  `Tda:LobesConfigPath` at launch time.

**Implementation gap:** The startup help text and comments in `Program.cs` state that
LOBEs load from `<BaseDir>/{port}/lobes/`, but the actual default in code is:

```csharp
opts.LobesConfigPath = ctx.Configuration["Tda:LobesConfigPath"]
    ?? Path.Combine(AppContext.BaseDirectory, "lobes", "lobes.config.json");
```

This is the shared `lobes/` directory, not port-scoped. The documentation is ahead of
the implementation. The default must be changed to
`Path.Combine(AppContext.BaseDirectory, port.ToString(), "lobes", "lobes.config.json")`
and the build/deployment scripts updated to copy LOBEs into the port-scoped directory
at launch time.

**What will be needed:**
- Default `LobesConfigPath` changed to `{port}/lobes/lobes.config.json` so each
  instance has its own LOBE directory.
- Build or launch scripts updated to seed `{port}/lobes/` from the shared catalog on
  first run (or copy at build time per port).
- A LOBE installer / package manager that downloads `.lobe.json` + `.psm1` pairs
  from a registry and places them in `{port}/lobes/`.
- `LobeManager` hot-reload (TDA-001 ✓) is already in place — marketplace installs
  will not require a TDA restart once TDA-004 is done.
- Possibly a signed/verified download chain so only trusted LOBEs are installed.

**No code change required now** — tracked here for design continuity.

---

## ~~TDA-003~~ — Wanderer-first additive role architecture ✓ *implemented*

**Area:** `Program.cs`, `TdaOptions`, `Svrn7.Core/Models.cs`, deployment

**Summary:** Every TDA starts as a **Wanderer** (`Svrn7Role.Wanderer`). Role is
additive — promoting a TDA creates an *additional* DID alongside the primary
Wanderer DID. The Wanderer DID is always the primary identity.

- **Wanderer** — base role; every TDA. Auto-bootstrapped on first run.
- **Federation** — Wanderer + `Initialize-Svrn7Federation` (no params). One per deployment.
- **Society** — Wanderer + Society DID registered with a Federation TDA via DIDComm.
- **Citizen** — Wanderer + Citizen DID registered with a Society TDA via DIDComm.

The `--role` and `--did` CLI arguments have been removed. `--port` is the only
required startup parameter. Role detection is DB-driven: `GetFederationAsync()`,
`GetOwnSocietyAsync()`, or equivalent. Each TDA is isolated via its port-scoped
`{port}/mem/` data directory. `Svrn7Name` is stored in the DIDDocument and
auto-generated as `"TDA-{port}"` at first-run Wanderer bootstrap.

---

## ~~TDA-002~~ — Federation/society query via DIDComm ✓ *implemented*

Protocol `federation/1.0/society-list` → handler `Invoke-Web7SocietyList` in
`Svrn7.Federation` LOBE.  Returns count, activeCount, and a societies array to
the sender's DID Document endpoint.  See LOBEDEBUG.md §4.5 for the send pattern.

---

## ~~TDA-001~~ — Hot-reload for JIT LOBEs ✓ *implemented*

**Area:** `LobeManager`, `IsolatedRunspaceFactory`

**What was needed and how it was implemented:**

- `FileSystemWatcher` on the lobes directory (`_watcher`) — detects new `*.lobe.json`
  files at runtime and auto-registers them via `RegisterFromDescriptor`. ✓
- `FileSystemWatcher` on `lobes.config.json` (`_configWatcher`) — detects changes to the
  eager LOBE list; warns if new eager entries are added (restart required for those). ✓
- JIT LOBE hot-reload — achieved via `Import-Module -Force` on every dispatch in
  `EnsureLoadedAsync`. A changed `.psm1` is picked up automatically on the next message
  without a TDA restart. No dirty-flag or runspace drain is needed; Force reimport
  is the mechanism. ✓

**Remaining constraint:** Eager LOBEs (baked into the `InitialSessionState` at startup)
still require a TDA restart when their `.psm1` changes. This is by design — the ISS
cannot be rebuilt at runtime.

---

## TDA-001b — Eager LOBE re-verification cost per dispatch (FYI / Design Note)

**Area:** `LobeManager.EnsureLoadedAsync`, `DIDCommMessageSwitchboard.InvokeCmdletPipelineAsync`

**Summary:** Eager LOBEs are reimported via `Import-Module` on every message dispatch,
even though they are already present in the runspace via the `InitialSessionState` (ISS).
Observed cost: ~43ms per dispatch (e.g. `Svrn7.Federation` on a `society-list` message).

**Why it happens:** `InvokeCmdletPipelineAsync` calls `EnsureLoadedAsync` for all
non-`.ps1` LOBEs unconditionally. The code comment says eager LOBEs are skipped — the
comment is wrong; the skip is not implemented.

**Why the current behaviour is intentional:** `Runspace.Open()` silently swallows ISS
load failures. Calling `Import-Module` for eager LOBEs on every dispatch acts as a
health check — if the ISS load failed silently, `EnsureLoadedAsync` detects it and
throws a clear `InvalidOperationException` rather than a cryptic "command not found"
error downstream.

**Trade-off:**
- Skipping → saves ~43ms per dispatch, but loses silent ISS failure recovery.
- Keeping → ~43ms overhead per dispatch per eager LOBE, but ISS failures surface
  immediately with a clear error message.

**Decision: keep current behaviour** during early development while ISS load
reliability is still being established.

**Future option:** Call `EnsureLoadedAsync` for eager LOBEs only when the runspace
probe (`IsolatedPipeline.ProbeRunspace()`) detects the module is missing — pay the
health-check cost only on actual failure, zero cost on the happy path.

---

## TDA-001a — JIT LOBE reimport cost per dispatch — *Deferred to Epoch 1*

**Area:** `LobeManager.EnsureLoadedAsync`, `IsolatedRunspaceFactory`, `DIDCommMessageSwitchboard`

**Summary:** JIT LOBEs are reimported via `Import-Module` on every message dispatch (~30 ms overhead per message).
Because each dispatch opens a fresh `Runspace` from the shared `InitialSessionState`
(ISS), JIT LOBEs are never present in the new runspace — `EnsureLoadedAsync` always
runs `Import-Module` for them.

**Current behaviour:**
- Eager LOBEs: baked into the ISS at startup via `iss.ImportPSModule()`. `Import-Module`
  in `EnsureLoadedAsync` is idempotent (module already present) — near-zero cost per dispatch.
- JIT LOBEs: not in the ISS. `Import-Module` runs from disk on every dispatch — pays the
  full module load cost each time.

**Design trade-off (intentional):** Per-invocation runspace isolation is the priority.
A crash or runaway cmdlet in one runspace cannot affect any other concurrent dispatch.
The JIT reimport cost is the accepted price for that guarantee.

**Deferred to Epoch 1.** The 30 ms overhead is acceptable at Epoch 0 throughput.

**Fix when prioritised:** Dynamically add a JIT LOBE to the ISS template the first time it is
needed (requires rebuilding the ISS or maintaining a secondary ISS per LOBE set).
This is closely related to TDA-001 (hot-reload) — the same ISS rebuild mechanism
would eliminate the per-dispatch import cost for frequently-used JIT LOBEs.

## TDA-001c — Double GetMessageAsync call in LOBE cmdlets (FYI / Minor Performance Note)

**Area:** `DIDCommMessageSwitchboard`, `PandoMail.0.8.0.psm1`, all LOBE cmdlets that call
`$SVRN7.GetMessageAsync()` internally

**Summary:** The Switchboard pipeline calls `Dequeue-Svrn7Message -Did $did` before invoking
the LOBE cmdlet, then passes only the string `$MessageDid` by name. LOBE cmdlets that
need the payload (e.g. `Dequeue-PandoMail`) call `$SVRN7.GetMessageAsync()` again
internally — a second round-trip to the inbox store for the same message.

**Why it is minor:** `GetMessageAsync` caches `InboxMessageView` in `SvrN7RunspaceContext`
with a 24-hour TTL. The second call for the same DID URL is a dictionary lookup — no I/O.
The redundancy is structural but the practical cost is near-zero today.

**Potential fix:** Change LOBE cmdlet signatures to accept `[Parameter(Mandatory,
ValueFromPipeline)] [Svrn7.TDA.InboxMessageView] $Message` and let the pipeline carry
the already-fetched view. The Switchboard currently passes `-MessageDid $didUrl` as a named
parameter for all LOBE cmdlets; that would need to be dropped from the non-.ps1 branch of
`InvokeCmdletPipelineAsync` when changing a cmdlet to this signature. The redundant
`$MessageDid` parameter would need to be kept (optional, unused) or the Switchboard updated.

**Decision:** Deferred — the cache makes this cosmetic at Epoch 0 throughput. Revisit if
profiling shows inbox-store reads appearing under load.

## TDA-008 — Version-less protocol URI fallback ("pick highest installed LOBE version")

**Area:** `LobeManager.TryResolveProtocol`, `LobeProtocolRegistration`

**Summary:** When a DIDComm message arrives with a version-less `@type` URI
(e.g. `did:drn:svrn7.net/protocols/PandoMail/signal-message` instead of
`did:drn:svrn7.net/protocols/PandoMail.0.8.0/signal-message`), the
Switchboard currently dead-letters it — no registration matches.  A possible
convenience feature would add a third fallback tier to `TryResolveProtocol`
that strips the version segment from all registered URIs, matches on LOBE name
+ action suffix, and routes to the highest installed version.

**What implementation would require:**
- Add `string LobeVersion` to `LobeProtocolRegistration` (already available
  from `descriptor.Lobe.Version` at registration time — just not carried through).
- Add ~30-40 lines to `TryResolveProtocol`: detect version-less incoming URI,
  collect candidate registrations by `LobeName` + action suffix, pick highest semver.
- Gate behind `TdaOptions.AllowVersionlessFallback` (default: `false`).

**⚠ WARNING — do not enable by default.**

The version segment in a protocol URI is a contract identifier.  Sender and
receiver must agree on the same message schema.  "Pick highest" silently breaks
that agreement:

- A message built against `0.8.0`'s schema arrives version-less.
- `0.9.0` is installed with a renamed or required field.
- The LOBE misparses the body — no routing error, silent data loss or panic
  inside the handler.

This is exactly the failure mode that protocol versioning prevents.
Version-less routing trades correctness for convenience and must never be the
default.  Dead-lettering version-less messages (P-006) is the correct default.

**Acceptable use:** opt-in for development tooling and single-version
deployments where "highest" is always "only".  Never in production with
multiple versions installed.

---

## TDA-009 — WebSocket /localcomm-ws channel encryption (PandoMail ↔ Citizen TDA) ✓ RESOLVED BY POLICY

**Area:** `WebSocketNotifyHub`, `KestrelListenerService`, `TdaMailClient`, `DIDCommPackingService`

**Policy decision (2026-06-19):** All WebSocket messages, inbound or outbound, use
plaintext DIDComm (`application/didcomm-plain+json`).  The `/localcomm-ws` channel
is localhost-only, PandoMail holds no key material, and PandoMail shares the Citizen
TDA's DID — it is a local UI attachment, not a DIDComm peer.  This is the permanent
design, not a temporary gap.

**Companion policy:** All outbound messages over HTTP (TDA-to-TDA) use SignThenEncrypt.
The WebSocket channel is explicitly excluded from that policy.

If PandoMail ever runs on a separate host the channel design must be reconsidered.
The preferred approach at that point is an ephemeral per-connection session key
returned in the WebSocket handshake response — no long-lived secret stored in PandoMail.

---

## TDA-010 — `PackSignedAsync` secp256k1 signing path ✓ DONE

**Area:** `DIDCommPackingService`, `DIDCommMessageSwitchboard`, `Program.cs`, `TdaOptions`

**Implemented (2026-06-19):** Option 1 (secp256k1 path, no identity changes).

Changes made:
- `PackSignedAsync` and `PackSignedAndEncryptedAsync` now accept `bool secp256k1 = false`.
  When `true`, the JWS header uses `alg = "ES256K"` and signing uses `NBitcoin.Key.Sign`
  (SHA-256 hash of input, DER-encoded signature).  The `alg = "ES256K"` verify branch in
  `UnpackJwsAsync` was already present — this completes the round trip.
- `TdaOptions.AgentSigningPrivateKey` (byte[]) added — holds the secp256k1 private key
  for the lifetime of the TDA process.
- `Program.cs` now loads `AgentSigningPrivateKey` from `agent-identity.json` at startup
  (first-run and subsequent runs).
- `DIDCommMessageSwitchboard.PackOutboundAsync` applies `PackSignedAndEncryptedAsync`
  with `secp256k1: true` to all HTTP outbound messages (per the outbound pack policy).

---

## ~~TDA-011~~ — WebSocketNotifyHub subscription routing for multiple local-UI clients ✓ *implemented (2026-07-01)*

**Area:** `WebSocketNotifyHub`, `KestrelListenerService`, `TdaMailClient`

**Summary:** `WebSocketNotifyHub` currently broadcasts every push notification to all
connected clients. This is correct when only one app is connected, but breaks with
multiple simultaneous local-UI apps (e.g. PandoMail + PandoBoard, or two PandoMail
instances). PandoBoard would receive Email-Notify messages it cannot handle; two
PandoMail instances would both update their UI on the same notification. It also
breaks request/reply correctness: today a reply to one client's `List-Emails` (etc.)
is broadcast to every connected socket, not routed back to the requester alone — invisible
today only because just one app connects at a time in practice.

**Finalized v1 design (2026-07-01) — static subscriptions declared at Hello:**

Each client sends a `Hello` envelope immediately after `ConnectAsync()` succeeds, before
any other traffic — modeled on the `attach`/`detach` pattern in
`src/WsExample2-Kestrel` (see that project's `docs/ARCHITECTURE.md` for the reference
implementation this borrows from):

```json
{
  "typ": "application/didcomm-plain+json",
  "id": "did:drn:svrn7.net/didcomm/msg/<guid>",
  "type": "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Hello",
  "from": "<TDA's own DID>",
  "to": ["<TDA's own DID>"],
  "body": {
    "app": "PandoMail",
    "appVersion": "0.8.0",
    "appFullName": "<Assembly.GetName().FullName>",
    "instanceId": "<client-generated guid, stable across reconnects>",
    "mvid": "<Module.ModuleVersionId, regenerated every compile>",
    "subscriptions": [
      { "uri": "did:drn:svrn7.net/protocols/Email-Notify.0.1.0/", "match": "prefix" },
      { "uri": "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Notify-FolderCounts", "match": "exact" }
    ]
  }
}
```

The `{ uri, match }` subscription entries deliberately reuse the same
`match: "exact"|"prefix"` shape already used in `.lobe.json` protocol registrations, so
the hub can reuse `LobeManager`'s existing longest-prefix-wins matcher instead of a
second implementation. `mvid`/`appFullName`/`instanceId` are adopted from
`WsExample2-Kestrel`'s `attach` message — they let operators correlate a live connection
to the exact deployed binary, and distinguish two windows of the same app.

The hub replies with an ack:

```json
{ "type": "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Subscribed", "body": { "subscriptions": [ /* echoed back */ ] } }
```

A symmetric `Goodbye` (mirroring WsExample2's `detach`) is sent by the client before a
clean disconnect, for clean server-side logging — not relied upon for correctness (the
WS close frame is authoritative).

**Fail-closed, no grace period (resolves the "decision deferred" note from the original
draft of this entry):** A socket with no recorded subscription receives nothing — no
timers, no "unfiltered until Hello arrives" fallback. This is safe specifically because
`Hello` is sent by client code we control (`TdaMailClient.ConnectAsync`) as its own last
step before returning, never left to a caller to remember.

**Hello is intercepted below the Switchboard, not routed through it:** LOBE cmdlets only
ever see `$SVRN7` and a message DID — they have no notion of *which socket* a message
arrived on, so subscription bookkeeping cannot be a LOBE-registered protocol. `Hello` (and
`Goodbye`) must be intercepted directly in `KestrelListenerService.ReceiveWebSocketLoopAsync`
using the `clientId` already returned by `_hub.Attach(ws)`, and never forwarded to
`_inbox.EnqueueAsync`/the Switchboard at all. This is the reusable part of the pattern:
subscription bookkeeping lives entirely in shared TDA infrastructure, decoupled from any
app's business protocol — PandoBoard (or any future local-UI app) gets it for free by
sending one `Hello` frame, no per-app server code required.

**Two distinct traffic categories on this channel — both implemented:** Topic
subscriptions solve *broadcast notifications* (`Email-Notify`, `Notify-FolderCounts`).
*Request/reply* correctness (`List-Emails`→`Get-PandoMails`, `Query-TdaDid`→`Reply-TdaDid`,
etc.) needed a separate mechanism — `WebSocketNotifyHub.TrackCorrelation(correlationId,
socketId)`, called from `KestrelListenerService.ProcessWebSocketMessageAsync` whenever an
inbound WS message's body carries a `correlationId`, before enqueueing. `PushAsync` checks
for a tracked correlation first (unicast to that connection only, entry consumed on match)
and falls back to subscription-based multicast otherwise. Entries older than 5 minutes are
pruned opportunistically on each `TrackCorrelation` call — bounds memory from requests that
never got a reply, no background timer needed.

**Note (2026-07-01):** `correlationId` lives in `body` because that's what every existing
LOBE already uses — it is *not* DIDComm V2's spec-standard `thid` (thread ID) field.
`Svrn7.DIDComm`'s `DIDCommMessage`/`DIDCommUnpackedMessage` never modeled `thid` at the
envelope level at all, so there was nothing to reuse; the correlation-routing work here
plugs into the existing ad-hoc convention rather than migrating every LOBE to a
spec-conformant field. See TDA-014.

**Multiple instances of the same app:** Two PandoMail instances both subscribe to
`Email-Notify`. Both receive the push (fan-out). Because the payload is a LiteDB
ObjectId reference, not a copy of the message body, reading the same record twice is
safe — no duplication risk.

**Inbound flow is unchanged for everything except Hello/Goodbye:** every other inbound
frame from a local client follows the existing path unchanged:

```
WebSocket frame received
  └── ReceiveWebSocketLoopAsync assembles complete message
        ├── if type == Svrn7.LocalUI.0.1.0/Hello|Goodbye → handled directly by the hub, not enqueued
        └── else → ProcessWebSocketMessageAsync
              ├── UnpackAsync  (plaintext — extracts @type, From, Body)
              └── EnqueueAsync → svrn7-msg.db
                    └── Switchboard drain loop
                          └── LobeManager.TryResolveProtocol(@type)
                                └── LOBE cmdlet invocation
                                      └── OutboundMessage
                                            └── Switchboard PackOutboundAsync
                                                  └── PushAsync (PeerEndpoint == LocalEndpoint)
                                                        └── routed to subscribed clients only
```

**Implemented (2026-07-01):**
- `WebSocketNotifyHub`: per-connection `Connection` record (`WebSocket`, per-connection
  `SendLock`, `Subscriptions`, `LastReceived`, `App`/`AppVersion`/`InstanceId`).
  `TryHandleControlFrameAsync` handles Hello (records subscriptions, replies `Subscribed`)
  and Goodbye (log only). `PushAsync` does correlation-unicast-first, then
  subscription-multicast. `MatchesSubscription` reuses the exact/prefix semantics from
  `.lobe.json` (not literally `LobeManager`'s matcher — a small local reimplementation,
  since subscriptions are a per-connection list rather than a single registry and only need
  an any-match boolean, not longest-prefix-wins).
- `KestrelListenerService`: `ReceiveWebSocketLoopAsync`/`ProcessWebSocketMessageAsync` now
  thread `clientId` through; `TryHandleControlFrameAsync` is checked before
  `UnpackAsync`/`EnqueueAsync`; `TryTrackRequestCorrelation` extracts `body.correlationId`
  and calls `_hub.TrackCorrelation` before enqueueing.
- `TdaMailClient`: `SendHelloAsync` (app=`PandoMail`, subscriptions = `Email-Notify.0.1.0/`
  prefix + `PandoMail.0.8.0/Notify-FolderCounts` exact) called as the last step of
  `ConnectAsync`. `DisconnectAsync` sends `Goodbye` then closes; wired to
  `MainForm.FormClosing`.
- Tests: `WebSocketNotifyHubTests` in `tests/Svrn7.TDA.Tests/TdaTests.cs` — fail-closed
  (no Hello ⇒ no broadcast), Hello ⇒ Subscribed ack, matching/non-matching prefix
  subscription, and correlated-reply-unicast-only (two connections, only the requester
  receives the reply).
- LOBEs: unchanged, as planned — still return `OutboundMessage` with
  `PeerEndpoint = WebSocketNotifyHub.LocalEndpoint`.
- `Send-LocalDIDCommMessage`: unchanged — no Hello sent, receives no pushes, as planned.

---

## ~~TDA-013~~ — /localcomm-ws WebSocket hardening (idle watchdog, message cap, per-connection send lock) ✓ *implemented (2026-07-01)*

**Area:** `WebSocketNotifyHub`, `KestrelListenerService`

**Summary:** Surfaced by reading `src/WsExample2-Kestrel` (a reference WebSocket
server/client pair) while designing TDA-011. That example treats three things as
non-negotiable for a production WS server; our `/localcomm-ws` channel currently has
none of them:

1. **No idle watchdog.** A PandoMail client that crashes without closing cleanly (no
   WS close frame sent) stays in `WebSocketNotifyHub`'s `_sockets` dictionary
   indefinitely — `PushAsync` will keep attempting sends to a dead socket until the
   TCP layer eventually notices. WsExample2's server watchdog (`Task.Run` polling every
   `WatchdogInterval`, default 5s) detects `DateTime.UtcNow - lastReceived > IdleTimeout`
   (15s), sends a `{"type":"timeout"}` notice, then does a graceful `CloseOutputAsync`
   half-close before falling back to a hard cancel if the client doesn't respond within 5s.

2. **No message size cap.** `KestrelListenerService`'s WS receive loop
   (`ReceiveWebSocketLoopAsync`) has no equivalent of the HTTP side's
   `kestrel.Limits.MaxRequestBodySize = 2 * 1024 * 1024` — an oversized or malformed
   frame accumulates in the `MemoryStream` reassembly buffer without bound. WsExample2
   checks `ms.Length > MaxMessageBytes` (1 MB) inside the reassembly loop and closes
   with `WebSocketCloseStatus.MessageTooBig` if exceeded.

3. **Global send lock, not per-connection.** `WebSocketNotifyHub._sendLock` is a single
   `SemaphoreSlim(1,1)` shared across *all* connected sockets — a broadcast to N clients
   serializes all N sends behind one lock, so one slow/stalled client's send blocks
   delivery to every other connected client. WsExample2 scopes the lock per-connection
   (`SemaphoreSlim sendLock` local to `HandleClientAsync`) specifically to avoid this;
   only sends *to the same socket* (echo reply vs. watchdog timeout message) need to be
   serialized against each other, not sends to different sockets.

**Implemented (2026-07-01), with one intentional simplification vs. WsExample2:**
- **Idle watchdog:** a single shared `System.Threading.Timer` in `WebSocketNotifyHub`
  (matching `IsolatedRunspaceFactory`'s epoch-refresh pattern already used elsewhere in
  this codebase) polls every 15s for connections idle over 60s, rather than one
  `Task.Run` per connection as in WsExample2 — simpler for the connection counts this
  channel actually sees. Sends a `Svrn7.LocalUI.0.1.0/Timeout` notice, then
  `CloseOutputAsync` (half-close) and relies on the client responding with its own close
  frame. **No hard-cancel fallback** if the client never responds (WsExample2 has one) —
  accepted for v1 since this channel serves a small, trusted set of first-party local
  processes, not adversarial peers; a stuck connection is caught eventually by the
  Detach/pruning already done in `PushAsync`.
- **Message size cap:** `WebSocketNotifyHub.MaxMessageBytes` (1 MB) checked inside
  `KestrelListenerService.ReceiveWebSocketLoopAsync`'s reassembly loop; closes with
  `WebSocketCloseStatus.MessageTooBig` if exceeded.
- **Per-connection send lock:** each `Connection.SendLock` guards sends to that socket only
  — replaces the old single global `_sendLock` that serialized broadcasts across every
  connected client regardless of which socket was actually slow.

**Bug found live and fixed (2026-07-02) — idle watchdog race dropping in-flight replies:**
`CloseOutputAsync` moves the *local* (server-side) `WebSocket.State` off `Open` without
cancelling an already-pending `ws.ReceiveAsync()` in `ReceiveWebSocketLoopAsync`. A real
user's PandoMail session showed this concretely: the watchdog logged
`connection ... idle for over 60s - closing` at `00:21:06`; a full minute later, at
`00:22:06`, PandoMail (which doesn't react to the `Timeout` notice at all — no dispatch
case for it) sent a new `Resolve-PandoDid` request on the same connection. The server's
still-pending `ReceiveAsync` accepted it, `UnpackAsync`'d it, enqueued it, and a LOBE
actually ran real DID-resolution work — but the outer `while (ws.State == WebSocketState.Open)`
loop condition failed on its very next check (state was already `CloseSent`), so the loop
exited and `Detach` ran *before* the reply was ready. When the LOBE's `Reply-DidDocument`
was finally ready to push: `Switchboard: pushed to local WebSocket (not connected).` —
silently lost, real work wasted, and PandoMail has no idea why that request never answered.

**Fix:** `ReceiveWebSocketLoopAsync` now re-checks `ws.State` immediately after fully
assembling a message, *before* dispatching it to `ProcessWebSocketMessageAsync`. If the
local half-close has already begun (state no longer `Open`), the message is dropped and
the loop breaks (`Detach` runs normally) instead of paying for LOBE work whose reply can
never be delivered.

**Related, not fixed here:** a message that arrives *validly* while the connection is
still `Open` and gets dispatched, but whose LOBE processing is still in flight when the
watchdog *independently* decides to close for idleness — a narrower, harder-to-hit variant
of the same class of bug, not covered by this fix. (The other related gap — `TdaMailClient`
not reacting to `Timeout` — is fixed by TDA-015 below.)

---

## TDA-015 — Retune idle/reconnect timeouts and add a heartbeat for real mail-client usage ✓ *implemented (2026-07-02)*

**Area:** `WebSocketNotifyHub`, `TdaMailClient`

**Summary:** TDA-011/013's timeout values were carried over fairly directly from
`src/WsExample2-Kestrel`, which is tuned for an actively-watched CLI echo-test tool, not a
background mail client meant to behave like Outlook. Two concrete problems followed from
that mismatch:

1. **60s idle timeout was too aggressive.** A user just reading mail generates zero
   outbound traffic for minutes at a time — completely normal usage that the watchdog
   couldn't distinguish from a dead connection, causing the exact TDA-013 race to fire on
   healthy connections.
2. **10 fixed-interval reconnect attempts (~10s total) gave up far too soon.** A TDA
   restart for a LOBE deploy, or any blip longer than ~10 seconds, left PandoMail
   permanently disconnected until the user noticed and manually triggered the old lazy
   reconnect-on-demand in `MainForm.LoadFolderAsync`.

**Implemented (2026-07-02):**

- **`Svrn7.LocalUI.0.1.0/Ping` → `.../Pong` heartbeat.** `TdaMailClient` sends `Ping` every
  `HeartbeatInterval` (20s) regardless of user activity; `WebSocketNotifyHub` intercepts it
  as a control frame (alongside Hello/Goodbye — never enqueued) and replies `Pong`. This
  keeps the server's idle clock fresh independent of application traffic, and gives the
  *client* proof the server is still alive too (see next point).
- **`IdleTimeout` raised from 60s to 10 minutes**, `WatchdogInterval` from 15s to 60s —
  now a generous backstop for non-heartbeating connections (crashed clients, one-shot tools
  like `Send-LocalDIDCommMessage`) rather than the primary liveness signal.
- **Client-side receive watchdog** (`TdaMailClient.ReceiveWatchdogLoopAsync`): tracks
  time since *anything* was last received (including `Pong`); if nothing arrives for
  `ReceiveTimeout` (60s, ~3× `HeartbeatInterval`), calls `ws.Abort()` to force the pending
  `ReceiveAsync` to fault, driving the existing `Disconnected`/reconnect path. This exists
  for a peer that vanishes with no WS close frame at all (true network black-hole) — a
  gracefully-closed or promptly-RST'd connection is already caught immediately by the
  normal receive-fault path (confirmed live, see below).
- **Reconnect changed from 10 fixed 1s attempts to unbounded capped exponential backoff**
  (1s, 2s, 4s, 8s, 16s, 30s, 30s, ... — `ReconnectBaseDelay`/`ReconnectMaxDelay` in
  `TdaMailClient`): retries for as long as the app is running rather than giving up
  permanently after ~10 seconds. `Reconnecting` event signature changed from
  `(attempt, maxRetries)` to `(attempt, nextDelay)` to match (no more fixed max);
  `ReconnectFailed` removed (nothing to fire — it no longer gives up).

**Live-verified (2026-07-01/02):** hard-killed a running TDA — reconnect fired
immediately with the correct backoff sequence (`1s → 2s → 4s → 8s → 16s → 30s → 30s`),
then `Reconnected after 7 attempt(s)` the moment the TDA was relaunched, with Hello/
Subscribed completing cleanly and Ping/Pong resuming on the new connection within one
heartbeat interval. (The receive watchdog itself wasn't exercised by this test — Windows
signalled the killed process's socket promptly enough that the normal receive-fault path
caught it first; the watchdog remains in place for the black-hole case that path can't
catch.)

---

## TDA-014 — Adopt DIDComm V2's `thid` instead of ad-hoc `body.correlationId` ✓ *implemented (2026-07-02)*

**Area:** `Svrn7.DIDComm` (`DIDCommMessage`, `DIDCommUnpackedMessage`), `Svrn7.Core`
(`InboundMessage`, `IInboxStore.EnqueueAsync`), `Svrn7RunspaceContext` (`InboundMessageView`),
`PandoMail.0.8.0.psm1`, `Svrn7.Identity.0.8.0.psm1`, `TdaMailClient`, `WebSocketNotifyHub`,
`KestrelListenerService`

**Summary:** Surfaced while implementing TDA-011's correlation-based reply routing
(2026-07-01). DIDComm Messaging V2 defines `thid` (thread ID) as a standard envelope-level
header — alongside `id`, `type`, `from`, `to`, `body` — used exactly for this purpose:
`id` identifies a message uniquely, `thid` (when present) points back to the `id` of the
message that started the thread. Every LOBE needing request/reply correlation (`Query-TdaDid`,
`List-Emails`, `Get-EmailBody`, `Resolve-PandoDid`, `List-OutboundEmails`, `List-DeadLetters`)
had invented the same thing by hand inside `body.correlationId`.

**Migration approach:** clean cutover (no transitional dual-support — every LOBE and client
here is first-party code, no external DIDComm peers depend on the old shape), full
`Svrn7.DIDComm` scope (not just the local WS channel).

**Implemented:**
- `Thid` added to `DIDCommMessage`/`DIDCommUnpackedMessage`/`DIDCommMessageBuilder` in
  `Svrn7.DIDComm`, threaded through all four pack methods and both unpack paths
  (`PlaintextResult`, `UnpackJwsAsync`).
- `Thid` added to `InboundMessage` (`Svrn7.Core`) and `IInboxStore.EnqueueAsync`'s new `thid`
  parameter, persisted by `LiteInboxStore`, populated from `unpacked.Thid` at both
  `KestrelListenerService` enqueue sites (HTTP and WS).
- `InboundMessageView` gained two fields: `Thid` (the incoming message's own thid, used by
  `Invoke-Svrn7DidResolveResponse`) and **`WireId`** (the sender's wire envelope `id` — see
  the bug note below).
- `KestrelListenerService.TryTrackRequestCorrelation` deleted entirely — replaced by a
  one-line `_hub.TrackCorrelation(unpacked.Id, clientId)` in `ProcessWebSocketMessageAsync`,
  no more hand-parsing `body.correlationId` out of JSON.
- `WebSocketNotifyHub.PushAsync` now reads top-level `thid` directly instead of digging into
  `body.correlationId` via `ExtractBody`/`GetString`.
- Every `PandoMail.0.8.0.psm1` reply-building cmdlet (`Invoke-PandoMailList`, `Get-TdaDid`,
  `Invoke-Svrn7EmailGetEmailBody`, `Invoke-PandoMailResolveDid`, `Invoke-PandoMailListSent`,
  `Invoke-PandoMailListDeadLetters`) sets envelope-level `thid` instead of `body.correlationId`.
  `Invoke-PandoMailResolveDid`'s escalation forward to the parent TDA keeps its existing
  `requestId`/`originalRequesterDid`/`originalRequestId` **body** fields unchanged (that
  inter-TDA relay chain in `Resolve-Svrn7Did`/`Invoke-Svrn7DidResolveResponse` was judged
  out of scope — see below) but now seeds them from the wire id instead of a removed
  `correlationId` variable. `Invoke-Svrn7DidResolveResponse`'s terminal WS-push branch (this
  TDA was the original requester) switched from `body.correlationId` to envelope `thid` to
  match what the hub now expects.
- `TdaMailClient`: every request method now generates its own envelope `id` first, registers
  it in `_pending` *before* sending, and matches replies by envelope `thid` (`ExtractThid`,
  reading the top-level field — no more body parsing). `EmailBody.CorrelationId` (write-only,
  never read anywhere) deleted along with it.
- Test stubs (`NullInboxStore`, `RecordingInboxStore`, `ThrowingResetInboxStore`,
  `StubDIDCommService`) updated for the new `IInboxStore.EnqueueAsync` parameter and to set
  `Id` so `TrackCorrelation` has something to key on.

**Deliberately out of scope:** the inter-TDA DID-resolution relay chain
(`Svrn7.Identity.0.8.0/did-resolve-request` ↔ `did-resolve-response`, handled by
`Resolve-Svrn7Did` and the non-terminal branch of `Invoke-Svrn7DidResolveResponse`,
backed by `PendingResolutionStore`) keeps its existing `requestId`/`originalRequesterDid`/
`originalRequestId` body-field convention untouched. That mechanism is multi-hop
(Citizen→Society→Federation), used by more than just PandoMail, not reachable in this
session's single-TDA live-verification setup, and wasn't what motivated TDA-011/TDA-014 in
the first place — migrating it carries real regression risk for a system capability with no
corresponding test coverage here, for no functional gain over the working `thid`-based local
boundary. Only the two points where this chain crosses into the local WS channel
(`Invoke-PandoMailResolveDid`'s local-reply branches and `Invoke-Svrn7DidResolveResponse`'s
terminal branch) were migrated, since those *do* need to match what `WebSocketNotifyHub`/
`TdaMailClient` now key correlation on.

**Bug found during live verification — `$msg.Id` vs `$msg.WireId`:** the first pass set every
reply's `thid` to `$msg.Id`, which is `InboundMessageView.Id` — the TDA's own internal storage
resource DID (`did:drn:/inbox/msg/...`), *not* the sender's wire envelope `id` that
`WebSocketNotifyHub.TrackCorrelation` actually keys on. These are two unrelated values that
happened to share a plausible-looking name. Because they never matched, every correlated
reply silently fell through `PushAsync` to the subscription-broadcast path, found no matching
subscriber, and was dropped — `Query-TdaDid` and `List-Emails` simply never got a reply, with
no error anywhere (the hub logged "push complete" regardless, since a broadcast to zero
matching connections isn't a failure). Caught by live end-to-end testing, not the unit suite
(the stubbed `StubDIDCommService` didn't set `Id` either, so the existing correlation test
passed for the wrong reason until it was also fixed). Fixed by adding `WireId` to
`InboundMessageView`, populating it from `InboundMessage.WireId` at all three construction
sites, and using `$msg.WireId` (never `$msg.Id`) everywhere a reply's `thid` is set.

**Live-verified (2026-07-02):** launched a real TDA + PandoMail, confirmed `Reply-TdaDid` and
`Get-PandoMails` arrive with `thid` correctly echoing the request's wire `id`. Then via a
scripted WS client: `List-DeadLetters`, `List-OutboundEmails`, and `Resolve-PandoDid` (local
hit/no-parent branch) all correlated correctly. Finally sent a real message to self via
`Enqueue-PandoMail`, listed it via `List-Emails`, and fetched its body via `Get-EmailBody` —
confirmed `thid` matched end-to-end for all six correlated request/reply protocols. Full test
suite (103 TDA tests, 62 core tests, 17 Society tests) passes; one pre-existing unrelated
failure (`RegisterSociety_DuplicateDid_Fails`) confirmed present on the clean tree before this
work and left untouched.

---

## TDA-012 — Third-party LOBE isolation: restrict $SVRN7 surface to constants

**Area:** `Svrn7RunspaceContext`, `IsolatedRunspaceFactory`, LOBE authoring guide

**Summary:** All LOBEs — first-party and third-party — currently receive the full
`$SVRN7` context object, which exposes inbox store access, dead-letter store access,
DID registry operations, the full `ISvrn7SocietyDriver` stack, and key-material-adjacent
fields such as `LocalDid` and `ServiceEndpointUrl`.

Ideally, third-party LOBEs should have access only to constant/read-only values
(e.g. `LocalDid`, `Role`, `CurrentEpoch`, `ServiceEndpointUrl`) and not to mutable
store operations or driver methods. The current design gives every LOBE equal trust
regardless of provenance — a third-party LOBE can call `EnqueueDeadLetterAsync`,
read the full inbox via `ListEmailsAsync`, or invoke any `Driver.*` method.

**Options to investigate:**
1. **Two-tier `$SVRN7`** — `$SVRN7` for first-party LOBEs (full surface, current);
   `$SVRN7Const` (or a reduced `$SVRN7`) for third-party LOBEs (constants only).
   Third-party flag sourced from `lobe.json` (`"trust": "third-party"`). Simplest to
   implement, coarsest — a third-party LOBE that legitimately needs `ResolveDidAsync`
   (see actual usage below) gets nothing.
2. **Interface split** — Define `ISvrn7LobeContext` (constants + GetMessageAsync only)
   and `ISvrn7TrustedLobeContext : ISvrn7LobeContext` (full surface).
   Inject the appropriate interface into the runspace based on LOBE trust level.
   Same granularity as option 1, but cleaner on the C# side (real interface
   segregation instead of a runtime flag check), at the cost of restructuring
   `Svrn7RunspaceContext`'s public API into two interfaces.
3. **Capability-based** — `lobe.json` declares required capabilities
   (`"capabilities": ["inbox.read", "deadletter.write"]`); the runtime grants only
   what is declared, and the operator approves the capability list at install time.
   Finest-grained and most honest about what each LOBE actually touches, but needs a
   capability→member mapping designed and enforced (likely a dynamic proxy or
   per-capability wrapper) and an install-time approval UX that doesn't exist yet.

**Actual usage today (2026-07-02):** grepped every LOBE's `.psm1` for `$SVRN7.Driver.*`
calls across all five LOBEs currently in the tree (PandoMail, Identity, Notifications,
Onboarding, UX). The entire real usage is three members, all read-only lookups:
`Driver.ResolveDidAsync`, `Driver.CreateDidDocument`, `Driver.SocietyDid`. Nothing in
the current LOBE set touches `TransferAsync`, `RegisterSocietyAsync`,
`ErasePersonAsync`, or any signing/key-generation method — yet all of those are
reachable from any LOBE today, first-party or third-party, since no trust distinction
exists anywhere in the codebase (`lobe.json` has no `trust`/capability field; grepped
and confirmed). The gap between what's exposed and what's needed is large, which is
exactly what makes a capability-based restriction (option 3) feasible without breaking
any existing LOBE.

**Leaning:** option 3, despite being the most work, because option 1/2's "third-party =
constants only" line would immediately break the `ResolveDidAsync`/`CreateDidDocument`
pattern several first-party LOBEs already use if any were ever reclassified as
third-party, or if a legitimate third-party LOBE needed DID resolution (plausible — DID
resolution is explicitly designed as an open service, see P-008). Capability
declarations let the trust boundary track actual need instead of an all-or-nothing
split.

**Why not fixed now:** no active vulnerability — no third-party LOBE loading mechanism
exists yet (no marketplace, no external LOBE install path in production use; see
TDA-004). Worth revisiting once TDA-004 (LOBE marketplace/registry) is closer to real,
since that's what actually introduces untrusted LOBEs into the picture.

**No code change required now** — note for future investigation.

---

## TDA-016 — Migrate PandoBoard to a live TDA connection (planning note, 2026-07-02)

**Area:** `Web7.SVRN7.Apps.PandoBoard` (new: a shared local-UI transport library;
new: a board content LOBE), `Svrn7.Presence.0.8.0` (reused, not modified)

**Summary:** PandoBoard (`src/Web7.SVRN7.Apps.PandoBoard`) is a WinForms + SkiaSharp
contacts/conversation board — a `BoardColumn` per `Contact`, each with
`ConversationThread`s and `Message`s — currently populated entirely by
`SeedDataService.CreateSeedColumns()` (a large static file of fake DID-community
contacts and canned conversations). No networking exists yet. This note plans the
migration to a live `/localcomm-ws` connection, the same way `TdaMailClient` connects
PandoMail today.

Two things make this migration better-positioned than a from-scratch one:

- **The domain model already speaks DIDComm.** `Contact.Did`, `ConversationThread.Thid`,
  `Message.SenderDid`/`DIDCommType`/`IsVerified` aren't generic chat-app fields — they
  map almost one-to-one onto DIDComm envelope concepts already. `ConversationThread.Thid`
  in particular should literally be the DIDComm `thid`, grouping stored messages by
  thread the same way TDA-014 now uses `thid` for reply correlation.
- **A presence protocol already exists and fits directly.** `Svrn7.Presence.0.8.0`
  (`status`/`subscribe`/`unsubscribe`, `Available`/`Busy`/`Away`/`Offline`, cached in
  `IMemoryCache`) maps onto `Contact.Status` (`Online`/`Away`/`Offline`) — PandoBoard
  doesn't need to invent presence, just consume this LOBE's `Get-Web7Presence`/
  `Publish-Web7Presence`/subscribe cmdlets. One reconciliation needed: the enum values
  don't line up 1:1 (`Available`/`Busy` on the Presence side vs. `Online` on the board
  side — decide where `Busy` maps).

**Planned steps:**

1. **Shared transport library vs. copy-paste `TdaMailClient`.** The first real fork in
   the road. `TdaMailClient` already has the heartbeat, capped-exponential-backoff
   reconnect, `thid`-based correlation, and Hello/Subscribed handshake that PandoBoard
   will need identically — it's the same `/localcomm-ws` protocol on the same hub.
   Copy-pasting means every future WS-hardening fix (see TDA-013/TDA-015 and the
   2026-07-02 robustness-review fixes) has to land twice and will drift. Recommended:
   extract the transport-agnostic parts (connect/reconnect/heartbeat/Hello/correlation
   dictionary/`SendEnvelopeAsync`) into a small shared library (e.g.
   `Svrn7.LocalUiClient`), leaving only the PandoMail-specific request methods in
   `TdaMailClient` and the PandoBoard-specific ones in a new `TdaBoardClient`.
2. **New protocol family + LOBE for board content.** Presence is covered, but
   conversation content isn't — nothing in the current LOBE set is a generic messaging
   protocol (PandoMail's is RFC 5322-email-shaped, wrong fit for a lightweight chat
   message). Needs something like `Svrn7.Board.0.1.0` or `PandoBoard.0.1.0` with cmdlets
   analogous to PandoMail's: `Query-Contacts`/`List-Contacts`, `List-Threads`,
   `Get-ThreadMessages`, `Send-BoardMessage`, plus a push type
   (`Board-Notify.0.1.0/new-message`) mirroring `Email-Notify`.
3. **Domain mapping is mostly a rename, not a redesign** — see the DIDComm-shaped
   fields already on `Contact`/`ConversationThread`/`Message` above.
4. **Replace the seam, not the surface.** `BoardForm` has exactly one injection point —
   `_columns = SeedDataService.CreateSeedColumns()`, followed by
   `_velocity.RefreshAndSort(_columns)` and `_surface.SetColumns(_columns)`. Swap the
   first line for an async live load; `VelocityService` and `BoardSurface` don't need
   to change.
5. **Incremental updates matter more here than in PandoMail.** PandoMail's
   reload-the-active-folder-on-notify approach is fine for a list view. PandoBoard is a
   live, always-visible multi-column canvas with actively animated column widths
   (`CurrentWidth`/`TargetWidth`) — a full reload on every `Board-Notify` push would
   reset scroll position and animation state. Push handling should patch the specific
   column/thread in place, not rebuild `_columns` wholesale.
6. **Bake in the exception-safety lesson from the start.** Wrap every push-notification
   handler in try/catch and add `Application.ThreadException`/
   `AppDomain.UnhandledException` handlers in PandoBoard's `Program.cs` from day one,
   rather than retrofitting after a crash the way PandoMail's were added reactively
   (2026-07-02 robustness review).
7. **DI makes this cleaner than PandoMail, not harder.** PandoBoard already uses
   `Microsoft.Extensions.DependencyInjection` (`Program.cs` builds a `ServiceCollection`)
   — unlike PandoMail, which deliberately avoids a DI container.
   `services.AddSingleton<TdaBoardClient>()` injected into `BoardForm`'s constructor is
   more idiomatic here than PandoMail's manual `new TdaMailClient(...)` field.
8. **Test the multi-app scenario, since it's real.** `WebSocketNotifyHub` already
   supports multiple simultaneous connections with independent subscriptions — CLAUDE.md
   explicitly describes this as reusable across apps. Once PandoBoard exists,
   verification should include PandoMail *and* PandoBoard connected to the *same* TDA at
   once (sharing the Citizen TDA's DID), confirming correlated replies and presence
   broadcasts route to the right one.

**Suggested order:** protocol/LOBE design first (needs cmdlet-shape decisions), then
the shared transport extraction, then the seam swap in `BoardForm`, then
incremental-update wiring, then the dual-app live test.

**No code change required now** — planning note, not yet scoped for implementation.

---

## TDA-017 — Promote an updated DID Document to the Society TDA on change

**Area:** `Svrn7.Identity` DID Document update path, `Svrn7.Society` membership,
a new outbound "DID Document publish" DIDComm protocol; related to
docs/AGENTWALLET.md §D11/§D12 (endpoint moves) and the deferred
`--republish-endpoint` mechanism.

**Summary:** When a TDA updates its own DID Document (key rotation,
service-endpoint change, `alsoKnownAs`, etc.), the change currently stays local —
every other tier that resolves or caches that DID keeps serving the stale
version. Each TDA should **propagate its updated DID Document along its tier
links** so the authoritative resolver at each tier stays current. The propagation
is hierarchical and applies at every level:

| Updating TDA | Push the new DID Document to |
|---|---|
| Wanderer / Citizen (Society member) | its **Society TDA** |
| Society | its **Federation TDA** (up) **and** its **member Citizen TDAs** (down) |
| Federation | its **member Society TDAs** (down) |

The downward push is a notify/refresh: a resolver that has cached a peer's
document should be told a newer version exists (or handed it directly).

**Shape:**

- Trigger: any successful `IDidDocumentRegistry.UpdateAsync` on the TDA's own DID.
  Fan-out is chosen from `TdaOptions.Role` and the known tier links —
  `ParentTdaDid` / `ParentTdaEndpointUrl` for the upward push, the Society
  membership store (Citizens) or Federation registry (Societies) for the
  downward push.
- Transport: an outbound DIDComm message carrying the new canonical
  `DocumentJson` + `Version`. One protocol, both directions, e.g.
  `did:drn:svrn7.net/protocols/Svrn7.Identity.0.8.0/did-document-publish`.
  SignThenEncrypt like every other TDA-to-TDA message; the receiver verifies the
  signature chains to a key in the *previous* version before accepting.
- Receiver side: validate `Version == current + 1`, signature, and that the DID
  is a known peer at that tier (active member / parent), then `UpdateAsync` its
  own copy.
- Downward fan-out to many Citizens: a bounded broadcast from the Society's
  outbox (batched, rate-limited); a lighter `did-document-changed` notify
  (DID + new version only, recipient pulls) is an option to avoid shipping full
  documents to every member on every change.
- Idempotency / ordering: ignore a version ≤ the one held; the sender retries on
  delivery failure (reuse the outbox).
- Interaction with §D12: an endpoint move (`--republish-endpoint`) is one case
  of a DID Document update, so it should ride this same publish path rather than
  invent a second one.

**Open questions:** whether the Federation needs each Society's rolled-up member
set refreshed, or only the Society's own document; full documents vs. a signed
diff vs. notify-and-pull; back-fill for peers that were offline when the update
happened (pull on next contact); loop/echo suppression when a tier both accepts
and re-broadcasts.

**No code change now** — backlog item.
