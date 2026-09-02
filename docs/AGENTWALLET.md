# AgentWallet & Per-Identity Runtime Storage — Design

**Status:** Accepted (design phase — not yet implemented)
**Scope:** TDA startup, identity storage, runtime folder layout, database encryption, listen-port lifecycle
**Supersedes:** port-keyed `<BaseDir>/{port}/mem/` layout and plaintext `agent-identity.json`

---

## 1. Summary

A TDA instance today is keyed to its `--port`: databases and `agent-identity.json`
live under `<BaseDir>/{port}/mem/`, next to the executable. This design replaces that
with:

1. **Per-identity runtime folders** under a user-level data root
   (`~/.web7-pando/`), named after the TDA's DID genesis hash — not the port.
2. **An encrypted, password-protected wallet** (`Svrn7.Trust.AgentWallet`) holding
   all key material, replacing plaintext `agent-identity.json`.
3. **All five LiteDB databases encrypted**, keyed from the wallet.
4. **A per-instance `lobes/` folder**, installed from a machine-level
   `~/.web7-pando/lobe-library/` package source — no shared *installed* LOBE
   catalog.
5. **Listen port auto-selected once** on first run, recorded in the DID Document,
   and never changed automatically thereafter.

The port stops being an identifier and becomes purely a listen setting.

---

## 2. Motivation

| Problem today | Consequence |
|---|---|
| Runtime folder named by `--port` | The same identity on a different port is a different data set; the port is load-bearing where it should be incidental. |
| Data lives under `AppContext.BaseDirectory` | A binary update/reinstall endangers live data. |
| `agent-identity.json` is plaintext | secp256k1 + X25519 private keys sit on disk in the clear. |
| LiteDB files are plaintext | DID Docs, message bodies, VCs readable from a stolen disk image. |
| Shared machine-wide `lobes/` catalog | One instance's LOBE upgrade affects every instance; no side-by-side versions. |

Goals: correlate the runtime folder with the DID; protect key material and data at
rest behind a password; isolate each instance's LOBE set; keep the published network
endpoint stable.

---

## 3. Why a new component, not "extend KeyWallet"

`Svrn7.Trust.KeyWallet` (already in the repo) is **ECDSA P-256, single key**:

- `KeyPair.Generate()` → `ECDsa.Create(nistP256)`
- `EcMath` hard-codes the P-256 curve parameters
- `WalletFile` v2 stores exactly one encrypted PKCS#8 blob
- `Mnemonic` derives a P-256 scalar from a BIP39 seed by a non-standard construction

SVRN7 needs:

- a **secp256k1** identity key — the DID genesis hash is `Blake3(secp256k1
  compressed pubkey)`, JWS is ES256K, transaction signing is secp256k1
- **plus** an **X25519** key-agreement key for inbound JWE decryption
- plus `did`, `role`, parent-TDA wiring, the recovery phrase, and a random DB master key

So the encrypted payload is generalised from "one PKCS#8 key" to **a JSON
document**. KeyWallet's key-type-specific classes (`KeyPair`, `EcMath`,
`Mnemonic`) are not reused; its key-type-agnostic protection machinery is.

`Svrn7.Trust.AgentWallet` is a **new standalone project**. It **copies** (does not
project-reference) the reusable KeyWallet files, so KeyWallet keeps its clean
single-P-256-key API. If a third consumer of the shared crypto ever appears,
extract a `Svrn7.Trust.WalletCore` then — not now.

### Borrowed from KeyWallet

| File / pattern | Reused as-is? | Notes |
|---|---|---|
| `WalletCrypto` | Verbatim | Argon2id (64 MiB / 3 passes / parallelism 4) + AES-256-GCM. Cost params embedded in the blob: `memKiB(4) ‖ iters(4) ‖ par(4) ‖ salt(16) ‖ nonce(12) ‖ tag(16) ‖ ciphertext`. Operates on arbitrary `byte[]`. PBKDF2 "v1" path retained for format lineage, unused by new wallets. |
| `WalletFile.Save` pattern | Adapted | Atomic write: serialise to `.tmp`, `stream.Flush(flushToDisk: true)`, `File.Replace(tmp, path, .bak)`; `PlatformNotSupportedException` → manual copy+move fallback. |
| `WalletFile.Load` pattern | Adapted | Corruption detection; error message points at the `.bak`. |
| `UnlockThrottle` | Verbatim | `.lockout` JSON sidecar. First 2 wrong attempts free; then `1 << (failures − 2)` seconds, capped at 300 s. Reset on successful unlock or any fresh write. Corrupt sidecar fails **open**. |
| `IPinStore` + trust-on-first-use | Adapted | Pin the **secp256k1** public-key hash (SHA-256 of the compressed pubkey). Mismatch → refuse before prompting. First use + enabled store → enrol after a successful password check. |
| `KeyWalletDiagnostics` pattern | Adapted | One `ActivitySource` + one `Meter`, both named `"AgentWallet"`, no exporter. Host opts in with `AddSource("AgentWallet")` / `AddMeter("AgentWallet")`. |

### Not reused

`KeyPair` (P-256 / `ECDsa`), `EcMath` (P-256 params), `Mnemonic` (P-256 seed
derivation). Key generation comes from `Svrn7.Crypto.CryptoService`
(`GenerateSecp256k1KeyPair`, `GenerateX25519KeyPair`, `Blake3Hex`); recovery from
`NBitcoin` (§9).

---

## 4. Decision log

Each decision below was made explicitly during design. Format: **Decision** —
rationale — consequences.

### D1 — Data root is `~/.web7-pando/`

**Decision:** All per-identity data moves under a user-level data root, default
`~/.web7-pando/` (`%USERPROFILE%\.web7-pando\` on Windows). Overridable by the
`PANDO_HOME` environment variable or a `--data-root <path>` flag.

**Rationale:** Decouples data from the executable location; survives binary
updates; conventional dotfolder location.

**Consequences:** `Program.cs` no longer derives paths from
`AppContext.BaseDirectory`. A machine may host many instances under one root.

---

### D2 — Runtime folder named `<name>-<genesisHash[..8]>`

**Decision:** One directory per identity, named
`<name>-<genesisHash[..8]>/`, where `<name>` is the sanitised (kebab-case)
`--name` argument and `<genesisHash>` is `Blake3(secp256k1 compressed pubkey)`
hex, first 8 characters.

**Rationale:** The genesis hash is **stable across role transitions**. A TDA
transitions Wanderer → Citizen → Society → Federation *in place*; the DID string
format changes per role (`wanderer.svrn7.net/agent/...` →
`<society>.svrn7.net/citizen/...`) but the key pair and therefore the genesis
hash do not. One folder = one key pair = one genesis hash = possibly several
role-DIDs over its lifetime. The `<name>-` prefix keeps a directory listing
human-scannable.

**Consequences:** The full DID cannot be the folder name (`:` and `/`; Windows
path length). 8 hex chars = 32 bits of collision resistance within one machine's
handful of instances — adequate; a collision is detected at creation (§D3) and
the slug lengthened.

---

### D3 — Instance discovery by directory scan (no `instances.json`)

**Decision:** No central index file. Each instance directory contains
`identity.meta.json` — cleartext, non-secret:

```jsonc
{
  "did":                   "did:drn:wanderer.svrn7.net/agent/1.0/<genesis-hash>",
  "name":                  "Wanderer1",
  "role":                  "Wanderer",
  "secp256k1PublicKeyHex": "<66 hex, compressed>",
  "serviceEndpointUrl":    "http://localhost:8440/didcomm",
  "createdAt":             "2026-09-01T00:00:00.000Z"
}
```

Startup resolves the instance by scanning `~/.web7-pando/*/identity.meta.json`
and matching on `--name` (or `--did`). If exactly one instance directory exists
and no selector is given, it is used.

**Rationale:** A central mutable file is a corruption point, needs write-locking
for concurrent instance creation, and drifts from what is actually on disk. The
`*.d/`-style scan is self-healing (drop a directory in → it appears; delete →
gone), needs no locking, and `n` is tiny.

**Consequences:** `identity.meta.json` is the **only** cleartext record of what an
instance directory holds — the DID Document (authoritative for the endpoint) is
now inside an encrypted database. `serviceEndpointUrl` in the meta file is a
mirror, rewritten whenever the endpoint changes.

---

### D4 — Relaunch with a changed `--name` fails

**Decision:** If `--name` resolves (via genesis hash) to an identity that already
has a directory under a *different* name, startup fails with
`did <did> already exists at <dir>`. No automatic rename.

**Rationale:** Silent directory renames hide operator error; an explicit failure
with the existing path is safer.

---

### D5 — `--reset` deletes the whole instance directory

**Decision:** `--reset` deletes the entire `<name>-<genesisHash[..8]>/` directory
(wallet, meta, `lobes/`, `mem/`) behind a confirmation prompt, forcing a clean
first-run bootstrap.

**Rationale:** Matches the current `--reset` intent (wipe and re-bootstrap) at the
new granularity.

**Consequences:** No migration tooling is built. `--reset` is the only supported
path from the old `<BaseDir>/{port}/mem/` layout to the new one (existing testnet
data is not carried over).

---

### D6 — Per-instance `lobes/` folder, installed from a machine-level library

**Decision:** Each instance has its own `lobes/` directory (and its own
`lobes/lobes.config.json`) under its slug folder. There is no shared *installed*
LOBE catalog. The **package source** is machine-level:
`~/.web7-pando/lobe-library/` holds the master copy of the LOBE NuGet packages
(`.nupkg`). An instance's `lobes/` is populated lazily — each LOBE is installed
from `lobe-library/` on first reference and cached (see below); each instance
selects and pins its own versions.

**Rationale:** LOBE version isolation — one instance can upgrade a JIT LOBE, or
run a different version, without affecting any other instance. A shared package
source means a fresh instance needs no network access when the package is already
cached. Aligns with the hot-reload and side-by-side-versioning backlog items.

**Install is the TDA's own job, lazily, on first reference.** The TDA installs a
LOBE package from `lobe-library/` into `<slug>/lobes/` itself — a LOBE `.nupkg`
is a plain zip (`tools/{Id}/…`), so `LobeInstaller` just extracts it; no NuGet
client, no external tooling. A package is installed only when first referenced
and stays in `<slug>/lobes/` thereafter, so it is extracted once per instance
and every later run loads it straight from `<slug>/lobes/`.

This is true for **both** LOBE kinds — the eager/JIT distinction is about *when a
LOBE is loaded into a runspace*, not *when its package is fetched*:

- **Eager (preloaded) LOBEs** — the eager list is walked at startup; each entry
  not already in `<slug>/lobes/` is downloaded and installed, then loaded into
  the `InitialSessionState`. "First reference" for an eager LOBE is that startup
  walk.
- **JIT LOBEs** — installed the first time a message for one is dispatched, then
  loaded with `Import-Module -Force` per dispatch as today.

**Source is `lobe-library/` only.** If a referenced package (or version) is not
in `lobe-library/`, the TDA **hard-fails** with a message telling the operator to
Publish it there. No remote-feed fallback for now (§14).

**Consequences:** Installed LOBE modules are duplicated per instance (disk cost,
accepted). `Program.cs` `LobesConfigPath` default moves from
`<BaseDir>/lobes/lobes.config.json` to `<slug>/lobes/lobes.config.json`. The
default eager list is a `lobes.config.json` **embedded in the `Svrn7.TDA`
assembly**, materialized to `<slug>/lobes/lobes.config.json` on first run by
`LobeManager.LoadLobeConfig` and operator-editable (hot-reload) thereafter.
`lobe-library/` is populated only by `PublishLOBEsToLibrary` (§D16); a remote-feed
fallback is out of scope (§14).

---

### D7 — Encrypted wallet payload is a JSON document

**Decision:** `agent-identity.wallet` stores an `AgentWalletFile` envelope with a
cleartext header and one encrypted blob; the blob is a JSON document, not a bare
key. See §7–8.

**Rationale:** SVRN7 identity is two keys of two curves plus metadata (§3).

---

### D8 — Database key: a stable random master inside the sealed payload

**Decision:** A random 32-byte `dbMaster` is generated at wallet creation and
stored as a plain `dbMasterKeyHex` field **inside** the wallet payload — which is
itself AES-256-GCM-sealed under the Argon2id password key, so `dbMaster` is
protected by the same key and the same single KDF pass as the private keys. All
five LiteDB files open with `Password = hex(dbMaster)` (as
`Filename="…";Password=…`).

*(An earlier draft additionally re-wrapped `dbMaster` under a second
password-derived key. That was dropped: it doubled the Argon2id cost at every
unlock for no gain — if the sealed payload plaintext ever leaks, the private keys
leak with it, so the extra wrap protects nothing this threat model cares about.)*

**Rationale:** `dbMaster` is a **stable** value. A password change re-seals the
payload (which must happen anyway, for the private keys) but does not change
`dbMaster`, so **the databases are never re-keyed** on a password change.
Deriving the DB password directly from the wallet password would instead force a
full LiteDB `Rebuild` of all five files every time the password changed.

**Consequences:** DB key rotation (only on suspected key compromise) is a
separate, explicit, rare operation: new `dbMaster` → `Rebuild` all five with the
new value → rewrite the wallet. `AgentWalletService.RotateDatabaseKey` returns
`{OldKey, NewKey}` for exactly this.

---

### D9 — All five databases encrypted

**Decision:** `svrn7-dids.db`, `svrn7-schemas.db`, `svrn7.db`, `svrn7-msg.db`,
`svrn7-vcs.db` — all use LiteDB native encryption (`Password=`).

**History:** An earlier draft left `svrn7-dids.db` / `svrn7-schemas.db` cleartext
(DID Documents are publishable anyway). Reversed: the *set* of DIDs an instance
knows, citizen rosters, and society membership are sensitive metadata even when
individual documents are public; and a single uniform key path is simpler.

**Consequences:**

- No cleartext inspection of any database. LiteDB Studio needs the password. An
  `agent db-shell` helper (derives the key after unlock) becomes the only
  inspection path.
- The listen-port read on subsequent runs now requires wallet unlock first
  (§D11), since the port lives in `svrn7-dids.db`.
- LiteDB takes an exclusive lock on an encrypted file — this doubles as the guard
  against double-mounting one identity (§11).
- **Overhead (estimate — not benchmarked in this repo):** storage ≈ 1 fixed
  header page, negligible; CPU/latency ≈ 10–15 % on mixed read/write with AES-NI,
  higher on cold-cache scans (every page fault also decrypts), near-zero on
  cache-hot reads. The DID-resolve path is on every inbound reply-routing —
  include it in any benchmark. Measure with representative data before relying on
  the figure.

---

### D10 — Recovery phrase: 12-word BIP39, Web7-owned BIP32 path

**Decision:**

- Library: `NBitcoin.Mnemonic` (standard BIP39; NBitcoin is already a transitive
  dependency via `Svrn7.Crypto`).
- **12 words / 128-bit entropy** for now. `bip39EntropyBits` is recorded in the
  payload so a later move to 24 words / 256-bit is non-breaking.
- Identity key derived via BIP32 (`NBitcoin.ExtKey`) at the **Web7-owned path
  `m/7'/0'/0'/0/0`** (purpose `7'`; deliberately not SLIP-0044-registered —
  phrases are Web7-internal and will not import into a standard BIP32/44 wallet).
- The **X25519 key** is re-derived from the same BIP32 seed:
  `HKDF-SHA256(ikm = seed, salt = "", info = "web7-pando/x25519/v1", L = 32)`,
  then X25519-clamped. One phrase restores both keys, hence the whole DID
  identity (genesis hash = `Blake3(secp256k1 pubkey)`).

**Rationale:** No role-based bases (see D14 rationale — TDAs transition in place).
KeyWallet's P-256 `Mnemonic` is unusable here; NBitcoin gives a real,
well-tested BIP39/BIP32 implementation for secp256k1 for free.

**Consequences:** `bip39EntropyHex` is stored in the encrypted payload so
`ExportRecoveryPhrase` can re-materialise the phrase after unlock. Keys imported
raw (no phrase) → `ExportRecoveryPhrase` returns "none".

---

### D11 — Listen port: auto-select once, then fixed

**Decision:**

- **First run only:** bind-with-retry starting at `--port-base` (default
  **8440**). Attempt the Kestrel bind; on `AddressInUseException` advance by one
  and retry, up to `--port-span` (default 64) attempts. The bind itself is the
  atomic claim.
- On the first successful bind, the actual port is written into **the local
  (Wanderer) DID Document `serviceEndpoint`** (`{url}:{port}/didcomm`) — the
  authoritative record — and mirrored into `identity.meta.json`.
- **Subsequent runs:** read the published port from the DID Document
  (`svrn7-dids.db`, after unlock) and bind **exactly** that port. If it is
  taken, **hard fail** with a message naming the published port — no
  re-selection.

**Rationale for "never auto-change":** other Web 7 ecosystem components cache DID
Documents. Silently rewriting a published `serviceEndpoint` breaks every cached
copy until it re-resolves.

**No role-based port bases (D14):** a TDA transitions into higher roles in place,
so a base chosen by role would be wrong after the transition.

---

### D12 — `--port` is optional; a conflicting `--port` is rejected

**Decision:** `--port` becomes optional (it is required today).

- First run, `--port` given → use it verbatim, skip the scan.
- Later run, `--port` matches the DID Document → proceed.
- Later run, `--port` **conflicts** with the DID Document → **reject, do not
  start**.

Moving a published endpoint would be a deliberate, separate operation
(working name `--republish-endpoint`: rewrite `serviceEndpoint`, bump the DID
Document `updated` timestamp / version so caches have a refresh signal,
re-publish to drn.directory where wired).

**Status: deferred.** The endpoint-move mechanism is out of scope for this phase —
its cache-invalidation and DNS-republish semantics need their own design. The
implemented behaviour now is **reject-only**: a conflicting `--port` on a later
run fails startup; the published port is always bound exactly. A TDA that must
move gets `--reset` + re-bootstrap until the move mechanism is built (§14).

---

### D13 — Password input: environment variable, else interactive

**Decision:**

1. If `PANDO_WALLET_PASSWORD` is set → use it, no confirmation.
2. Else → interactive prompt. On **first-run wallet creation**, prompt **twice**
   and require the entries to match.
3. If stdin is **not a TTY** (detached process, service, redirected) **and** the
   environment variable is absent → **fail fast** with a clear message; never
   block on an unanswerable prompt.

There is no `--password` flag and no `--password-file` — the environment variable
is the only non-interactive source.

**Rationale:** Interactive entry keeps the secret out of the process environment
block (readable by same-user processes via `/proc/<pid>/environ`, `ps eww`,
`ReadProcessMemory`). The environment variable is the automation escape hatch.
"Env if set, else prompt" gives humans the private path and scripts the
non-interactive one.

---

### D14 — Unlock throttle applies at startup

**Decision:** The `UnlockThrottle` backoff applies even at process startup. A
supervised service restarting in a loop with a bad secret will hit escalating
backoff — that is the intended signal.

**Rationale:** No special-casing; the throttle is a security control, not a
convenience toggle. (`--no-unlock-throttle` was discussed and not adopted; it can
be added later for supervised environments if needed.)

---

### D15 — `ISecretProtector` seam; Windows DPAPI now, others deferred

**Decision:** An optional at-rest cache for the wallet password (so restarts do
not re-prompt) sits behind:

```csharp
interface ISecretProtector {
    bool   Enabled { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] sealed);
}
```

v1 implementations:

| Platform | Implementation |
|---|---|
| Windows | `DpapiSecretProtector` — `System.Security.Cryptography.ProtectedData`, `CurrentUser` scope |
| everything else | `NullSecretProtector` — no caching; the env var or interactive prompt is required every start |

**Deferred behind the seam:** macOS/iOS Keychain (`kSecClassGenericPassword`,
Secure Enclave via `kSecAttrAccessControl`); Linux `systemd-creds` /
`LoadCredentialEncrypted=` (TPM2- or host-key-sealed) for headless services;
Linux desktop libsecret / Secret Service; kernel keyring.

**Rationale:** DPAPI is Windows-only and has no BCL cross-platform equivalent.
Mirrors KeyWallet's `IPinStore` "fail open to Null on unsupported platforms"
pattern. The cache is convenience only — never required for operation.

---

### D16 — Publish workflow targets `~/.web7-pando/`

**Decision:** Publishing `Svrn7.TDA` targets the data root and, in the same pass,
refreshes the LOBE library:

Publishing `Svrn7.TDA` runs **two independent tasks**:

| Task | Mechanism | Destination |
|---|---|---|
| Binaries | `web7-pando` publish profile (`Properties/PublishProfiles/web7-pando.pubxml`) — FileSystem, framework-dependent. Carries **no** LOBE packages. | `~/.web7-pando/bin/Debug/net8.0/` |
| LOBE packages | `PublishLOBEsToLibrary` MSBuild target (`AfterTargets="Publish"`) | `~/.web7-pando/lobe-library/` |

`lobe-library/` is a NuGet **local (folder) feed** — a directory of `.nupkg`
files is already a valid source, no server process. `PublishLOBEsToLibrary`
copies the **flat** `{id}.{version}.nupkg` layout that `LobeLibrary` reads;
changed packages overwrite, unchanged ones are skipped, so a rebuilt LOBE is
picked up. (`dotnet nuget push -s <folder>` would write the hierarchical
`<id>/<version>/…` layout instead — `LobeLibrary` does not parse that.)

**Invocation:** `Build ▸ Publish Svrn7.TDA ▸ profile "web7-pando"` in VS, or
`dotnet publish src\Svrn7.TDA -c Debug -p:PublishProfile=web7-pando`. Both tasks
run.

**Rationale:** One well-known location for the runnable TDA and the LOBE package
source, separate from the source tree, so a checkout/rebuild does not disturb a
running deployment. Per-instance `lobes/` folders install from `lobe-library/`;
the TDA process runs from `bin/`. **`PublishLOBEsToLibrary` is the only way
`lobe-library/` is populated** — the TDA never seeds it, and the binaries publish
never carries packages. An empty `lobe-library/` + a referenced LOBE = a hard
error telling the operator to publish it (§D6).

---

## 5. Directory layout

```
~/.web7-pando/                                    ($PANDO_HOME | --data-root override)
├── bin/                                          published TDA binaries (§D16)
│     └── Debug/net8.0-windows/                   Svrn7.TDA.dll + deps (config/TFM subpath)
├── lobe-library/                                 machine-level LOBE .nupkg package source (§D6)
└── <name>-<genesisHash[..8]>/                    one directory per identity
      ├── identity.meta.json                      cleartext, non-secret (§D3)
      ├── agent-identity.wallet                   AES-256-GCM / Argon2id (§7)
      ├── agent-identity.wallet.bak               previous version (atomic save)
      ├── agent-identity.wallet.lockout           UnlockThrottle state
      ├── lobes/                                   per-instance LOBE set (§D6)
      │     ├── lobes.config.json
      │     └── {Lobe}.{version}/…
      └── mem/
            ├── svrn7-dids.db                     ENCRYPTED — DID Documents (holds the port)
            ├── svrn7-schemas.db                  ENCRYPTED
            ├── svrn7.db                          ENCRYPTED
            ├── svrn7-msg.db                      ENCRYPTED
            ├── svrn7-vcs.db                      ENCRYPTED
            └── crash.log
```

The DPAPI secret cache (Windows) lives at its own
`%LOCALAPPDATA%\Web7Pando\` location, deliberately not inside the instance
directory.

---

## 6. `identity.meta.json`

Cleartext. Written at first-run bootstrap; `serviceEndpointUrl` rewritten if the
endpoint ever changes (`--republish-endpoint`). Contains **no secret material**.
Fields: `did`, `name`, `role`, `secp256k1PublicKeyHex`, `serviceEndpointUrl`,
`createdAt`. The DID Document inside `svrn7-dids.db` is authoritative for the
endpoint; this file is a discovery convenience.

---

## 7. `agent-identity.wallet` — envelope

```jsonc
{
  "Version":               1,                     // AgentWallet format version (independent of KeyWallet's)
  "Secp256k1PublicKeyHex": "<66 hex>",            // cleartext — pinning + discovery without unlock
  "EncryptedPayloadBase64":"<base64>",            // EncryptV2(utf8(payload), Argon2id(password, salt))
  "CreatedUtc":            "2026-09-01T00:00:00.000Z"
}
```

`EncryptedPayloadBase64` is exactly KeyWallet's `WalletCrypto.EncryptV2` output
format: `memKiB(4) ‖ iters(4) ‖ par(4) ‖ salt(16) ‖ nonce(12) ‖ tag(16) ‖
ciphertext`. Written via the atomic `.tmp` → `File.Replace(…, .bak)` pattern.

---

## 8. Encrypted payload

```jsonc
{
  "did":                   "did:drn:wanderer.svrn7.net/agent/1.0/<genesis-hash>",
  "role":                  "Wanderer",
  "createdAt":             "2026-09-01T00:00:00.000Z",

  "secp256k1PrivateKeyHex":"<64 hex>",            // 32 bytes — AgentSigningPrivateKey (outbound JWS)
  "secp256k1PublicKeyHex": "<66 hex>",            // compressed
  "x25519PrivateKeyHex":   "<64 hex>",            // 32 bytes — AgentKeyAgreementPrivateKey (inbound JWE)
  "x25519PublicKeyHex":    "<64 hex>",

  "parentTdaDid":          "",                    // optional
  "parentTdaEndpointUrl":  "",                    // optional

  "recoveryPhrase":        "<12 words>",          // the BIP39 phrase itself (§D10) — equally secret, no reconstruction step
  "bip39EntropyBits":      128,                   // forward-compat marker

  "dbMasterKeyHex":        "<64 hex>"             // 32 random bytes — stable LiteDB Password= for all five DBs (§D8)
}
```

- `dbMaster` is 32 random bytes, generated once at wallet creation.
  `Password = hex(dbMaster)` for all five LiteDB files (opened as
  `Filename="…";Password=…`). The value is not derived from the wallet password,
  so a password change never re-keys the databases.
- On unlock: one Argon2id pass derives the key; it decrypts the payload, and
  `dbMasterKeyHex` is read straight out of the decrypted JSON — no second wrap,
  no second KDF.
- On password change: re-derive the Argon2id key from the new password, re-encrypt
  the whole payload (`dbMaster` rides along unchanged), rewrite
  `agent-identity.wallet`. Databases untouched.

Both private keys and `dbMaster` are copied into `TdaOptions` / the DB connection
string and **never enter a LOBE runspace** (unchanged invariant). The Argon2id
key and the password `char[]` are zeroed immediately after use; the decrypted
payload bytes are zeroed once the fields are copied out.

---

## 9. `Svrn7.Trust.AgentWallet` — surface

```
AgentWalletService(walletPath, IPinStore, walletId? = null)
  Create(char[] password, Func<string,string> didFromGenesisHash, string role,
         string? recoveryPhrase = null, ...)         -> AgentIdentity
                              // derive/restore keys, generate dbMaster,
                              // write wallet + return it unlocked
  Unlock(Func<char[]> passwordProvider)              -> AgentUnlockResult
                              // throttle -> pin -> Argon2id -> decrypt payload
                              // -> read dbMasterKeyHex -> AgentIdentity
  ChangePassword(Func<char[]> current, char[] next)  -> void      // re-seal payload only; DBs untouched
  RotateDatabaseKey(Func<char[]> password)           -> DatabaseKeyRotation  // {OldKey,NewKey}; caller Rebuilds x5
  ExportRecoveryPhrase(Func<char[]> password)        -> string?   // null if imported raw
  Inspect()                                          -> WalletHeader   // no decrypt

AgentIdentity : IDisposable
  Did, Role
  Secp256k1PrivateKey : byte[32]        // zeroed on Dispose
  X25519PrivateKey     : byte[32]        // zeroed on Dispose
  DbMaster             : byte[32]        // zeroed on Dispose
  Secp256k1PublicKeyHex, X25519PublicKeyHex
  ParentTdaDid, ParentTdaEndpointUrl

UnlockResult = Success(AgentIdentity)
             | WrongPassword                 // throttle failure recorded
             | Throttled(TimeSpan retryAfter)
             | PinMismatch(byte[] pinned, byte[] actual)
             | NoWallet(string path)
```

Dependencies: `Svrn7.Crypto` (key generation, Blake3),
`Konscious.Security.Cryptography.Argon2`, `NBitcoin` (BIP39/BIP32 — transitive via
`Svrn7.Crypto`). **No** reference to `Svrn7.Trust.KeyWallet`.

---

## 10. Startup sequence

All of steps 1–7 run **before** `Host.CreateDefaultBuilder(...).ConfigureServices`,
because `AddSvrn7Society(...)` fixes every database path at service-registration
time.

1. **Parse args** — `--name` (or `--did`), optional `--port`, `--port-base`,
   `--port-span`, `--data-root`, `--url`, `--reset`, `--republish-endpoint`,
   optional `--recovery-phrase`.
2. **Resolve data root** — `--data-root` › `PANDO_HOME` › `~/.web7-pando`.
3. **Locate instance** — scan `<root>/*/identity.meta.json` for `--name` / `--did`.
   Exactly one directory + no selector → use it. None found → first-run.
   `--reset` → delete the resolved directory (confirm), then first-run.
4. **Obtain password** — `PANDO_WALLET_PASSWORD` if set; else interactive
   (double-entry on first-run create). Non-TTY + no env var → fail fast.
5. **First-run only:**
   a. `phrase` = `--recovery-phrase` or a fresh 12-word BIP39 phrase; derive
      secp256k1 (BIP32 `m/7'/0'/0'/0/0`) + X25519 (HKDF from the seed) keys;
   b. `genesisHash = Blake3(secp256k1 compressed pub)` → instance dir
      `<root>/<name>-<genesisHash[..8]>/`;
   c. generate a random 32-byte `dbMaster`;
   d. create `mem/` and `lobes/`;
   e. `AgentWalletService.Create` writes `agent-identity.wallet` (one sealed
      payload holding the keys, the phrase, and `dbMasterKeyHex`); Program.cs
      writes `identity.meta.json` after the port is bound.
6. **Unlock** (first and subsequent) — throttle → pin → one Argon2id pass →
   decrypt payload → read `dbMasterKeyHex` → copy secp256k1 + X25519 keys and
   `dbMaster` into `TdaOptions`; zero the password, the Argon2id key, and the
   decrypted payload bytes.
7. **Compute DB paths** — `mem/*.db` under the instance directory, each opened as
   `Filename="…";Password=hex(dbMaster)` (all five).
8. **`ConfigureServices` / `.Build()`** — unchanged wiring, new paths.
9. **Bind listen port:**
   - first run → bind-with-retry from `--port-base`; on success, patch the DID
     Document `serviceEndpoint` and the `identity.meta.json` mirror;
   - later run → open `svrn7-dids.db`, read the published port, bind exactly;
     taken → fail.
10. **DID Document creation** (first run) proceeds **after** the successful bind so
    the endpoint carries the real port.
11. Continue into the existing host lifecycle (`host.RunAsync()`).

### `TdaOptions` fields sourced from the wallet

Replacing the `agent-identity.json` reads in `Program.cs`:

| Field | Source |
|---|---|
| `AgentSigningPrivateKey` | payload `secp256k1PrivateKeyHex` (32 bytes) |
| `AgentKeyAgreementPrivateKey` | payload `x25519PrivateKeyHex` (32 bytes) |
| `LocalDid`, `Role` | payload / resolved DID Document |
| `ServiceEndpointUrl` | DID Document `serviceEndpoint` (bind result on first run) |
| `ParentTdaDid`, `ParentTdaEndpointUrl` | payload (if not set via config/env) |
| `AgentIdentityPath` | path to `agent-identity.wallet` |

---

## 11. Multiple concurrent instances

| Concern | Resolution |
|---|---|
| Distinct passwords per instance | `PANDO_WALLET_PASSWORD` is per-process; each launcher sets it in the child environment before starting that TDA. |
| Shared password across a testnet | Set once in the parent shell; children inherit. |
| Bootstrap write race | None — no shared mutable index (§D3). Each instance creates its own directory; the meta scan is read-only; `~/.web7-pando/` is read-mostly. |
| Port auto-select race (two first-runs at once) | Safe **because** it is bind-with-retry, not check-then-bind. The bind is the atomic claim; the loser of 8440 advances to 8441. |
| DID Document write ordering | `serviceEndpoint` is written **after** the successful bind, never before. |
| Double-mounting one identity | LiteDB takes an exclusive lock on the encrypted `mem/*.db`; the second process fails to open it. Surface `identity <name> is already running`, not a raw lock exception. |
| Interactive fallback, N instances | Each instance has its own console/stdin — N windows prompt independently. N first-runs = 2N prompts; use the env var to automate bulk bring-up. |
| `.lockout` / wallet file contention | Per-identity, single-process — none. |

---

## 12. Consequences & follow-ups

- **No cleartext database inspection.** Provide `agent db-shell` (derives the key
  after unlock).
- **`Program.cs` bootstrap is reordered** — port bind must precede DID Document
  creation on first run.
- **`LobesConfigPath` default** moves to `<slug>/lobes/lobes.config.json`.
- **LOBE install source** is `~/.web7-pando/lobe-library/` (machine-level
  `.nupkg` source). **Open (D6):** which set a fresh instance installs (a
  manifest vs. "all latest"), and how `lobe-library/` itself is populated from a
  remote feed.
- **Benchmark** the DB-encryption overhead on the DID-resolve and message-drain
  paths with representative data; the 10–15 % figure is an estimate.

---

## 13. Open (non-blocking)

- **Which LOBE set a fresh instance installs** — *resolved (unit 3)*: a default
  `lobes.config.json` (the eager list) is **embedded in the `Svrn7.TDA`
  assembly**; `LobeManager.LoadLobeConfig` materializes it to
  `<instance>/lobes/lobes.config.json` on first run, and the per-instance copy is
  operator-editable (hot-reload) thereafter. Each listed LOBE is installed from
  `lobe-library/` on first reference; a missing package is a hard error.

All §4 decisions are accepted. The endpoint-move mechanism is **deferred**, not
pending (§D12, §14).

---

## 14. Backlog (deferred)

| Item | Note |
|---|---|
| Endpoint-move mechanism (`--republish-endpoint`) | Deferred (§D12). Rewrite `serviceEndpoint` + bump DID Document `updated` + re-publish to drn.directory, with defined cache-invalidation semantics. Covers both the port and the `--url` host cases. Until built: `--reset` + re-bootstrap. |
| Remote LOBE feed fallback | Deferred (§D6). When a referenced package is absent from `lobe-library/`, fall back to a configured remote NuGet feed and cache it into `lobe-library/`. For now a missing package is a hard error. |
| Migration from `<BaseDir>/{port}/mem/` | Not built. `--reset` only. |
| `systemd-creds` / Keychain / libsecret `ISecretProtector` impls | Behind the seam (§D15). |
| `Svrn7.Trust.WalletCore` extraction | Only if a third consumer of the shared crypto appears. |
| 24-word / 256-bit recovery phrase | Format already forward-compatible via `bip39EntropyBits`. |
| Selective DB re-key / per-DB subkeys | Currently one `dbMaster` for all five files. |
| `--no-unlock-throttle` for supervised hosts | Not adopted (§D14). |
