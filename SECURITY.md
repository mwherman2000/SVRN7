# SVRN7 — At-Rest & Local Security

How a TDA instance protects its **key material and databases on disk and in
memory**: the password, the pin, the encrypted wallet, and the encrypted LiteDB
databases.

**In scope:** the AgentWallet, password handling, unlock throttling, public-key
pinning, in-memory key lifetime, LiteDB encryption, the BIP39 recovery phrase,
the at-rest secret cache, instance naming / port / isolation design, and the
local threat model.

**Out of scope (documented elsewhere):** DIDComm V2 pack/unpack, JWE/JWS, mTLS,
the SignThenEncrypt outbound boundary and decrypt-at-boundary inbound — that is
the *transport / message* security story in [CLAUDE.md](CLAUDE.md) and
[PRINCIPLES.md](PRINCIPLES.md) (P-008). The full design narrative for the layout
changes is [docs/AGENTWALLET.md](docs/AGENTWALLET.md).

---

## 1. The model in one paragraph

Every TDA identity lives in its own directory
`~/.web7-pando/<name>-<genesisHash8>/`. Its private keys and its database
encryption key are held **only** inside `agent-identity.wallet`, a single
AES-256-GCM blob sealed with an Argon2id key derived from an operator password.
On startup — before the host is built — the TDA unlocks the wallet, loads the
secp256k1 signing key, the X25519 key-agreement key, and a 32-byte database
master key into memory for the process lifetime, then zeroes the password and the
derived key. All five LiteDB databases are opened with that master key as their
`Password=`. Nothing secret is written in the clear; nothing secret enters a LOBE
PowerShell runspace.

---

## 2. File inventory and sensitivity

| Path (under the instance directory) | Contents | Sensitivity |
|---|---|---|
| `agent-identity.wallet` | Cleartext header (format version, secp256k1 **public** key hex, created-at) **+** one AES-256-GCM blob | **Secret** — the blob holds both private keys, the DB master key, and the recovery phrase |
| `agent-identity.wallet.bak` | The previous wallet file, kept by the atomic save | **Secret** — an *older* password/key; treat as the wallet |
| `agent-identity.wallet.lockout` | JSON: failed-attempt count + last-failure timestamp (`UnlockThrottle`) | Non-secret |
| `identity.meta.json` | `did`, `name`, `role`, `createdUtc`, and `parentTdaDid` once registered. **No endpoint URLs** (§11.3). | Non-secret — public locator + an opaque parent-tier pointer; the only cleartext record of the identity. Rewritten unconditionally every startup. |
| `mem/svrn7.db`, `svrn7-dids.db`, `svrn7-msg.db`, `svrn7-vcs.db`, `svrn7-schemas.db` | LiteDB databases | **Encrypted** (AES) — key derived from the wallet |
| `mem/*-log.db` | LiteDB write-ahead logs (one per database) | **Encrypted** — same key as their parent database |
| `mem/crash.log` | Startup exception detail | Low — stack traces, no key material |
| `%LOCALAPPDATA%\Web7Pando\AgentWallet\pin-store.bin` (Windows) | DPAPI-sealed map of `walletId → SHA-256(pubkey)` | Non-secret (hashes of public keys), sealed to the OS user; **not** kept next to the wallet |

The genesis-hash slug in the directory name is `Blake3(secp256k1 compressed
public key)[..8]` — derived from a **public** key, not secret, stable across
role transitions (Wanderer → Citizen → Society/Federation).

---

## 3. The wallet — `Svrn7.Trust.AgentWallet`

### 3.1 On-disk format (`agent-identity.wallet`)

```jsonc
{
  "version":                1,          // AgentWallet format version
  "secp256k1PublicKeyHex":  "<66 hex>", // cleartext — for pinning + directory discovery without unlock
  "encryptedPayloadBase64": "<base64>", // the sealed blob (layout below)
  "createdUtc":             "…"
}
```

The sealed blob is:

```
memoryKiB(4)  ‖  iterations(4)  ‖  parallelism(4)   ← Argon2id cost header
salt(16)  ‖  nonce(12)  ‖  tag(16)  ‖  ciphertext   ← AES-256-GCM
```

The Argon2id cost parameters are stored **in the blob**, so the cost can be
raised later without a new format version.

### 3.2 The encrypted payload (never written in the clear)

| Field | Purpose |
|---|---|
| `secp256k1PrivateKeyHex` | 32 bytes — DIDComm ES256K signing (`AgentSigningPrivateKey`), transaction signing, genesis-hash derivation |
| `x25519PrivateKeyHex` | 32 bytes — inbound JWE decryption (`AgentKeyAgreementPrivateKey`) |
| `secp256k1PublicKeyHex`, `x25519PublicKeyHex` | the matching public keys |
| `recoveryPhrase` | the 12-word BIP39 phrase (see §7) |
| `bip39EntropyBits` | `128` — forward-compatibility marker |
| `dbMasterKeyHex` | 32 random bytes — the LiteDB `Password=` for every database (see §6) |
| `did`, `role`, `createdUtc` | identity metadata |
| `parentTdaDid` | optional tier pointer — a DID only; the parent's endpoint is resolved from its DID Document at startup and is never persisted (§11.3) |

### 3.3 Key derivation and encryption

| Step | Primitive | Parameters |
|---|---|---|
| Password → key | **Argon2id** (`Konscious.Security.Cryptography.Argon2`) | memory **64 MiB** (65 536 KiB), **3** iterations, parallelism **4**, 16-byte random salt, 32-byte output |
| Seal payload | **AES-256-GCM** (`System.Security.Cryptography.AesGcm`) | 12-byte random nonce, 16-byte authentication tag |

Comparable to a desktop password manager's defaults. The GCM tag makes a wrong
password or a tampered blob fail **loudly** (`CryptographicException`) rather than
returning garbage. One Argon2id pass per unlock covers both the payload and the
DB master key (the DB key is a plain field inside the same sealed blob — no
second wrap, no second KDF).

### 3.4 Atomic write

`agent-identity.wallet` is written via a temp file: serialize → `.tmp` →
`stream.Flush(flushToDisk: true)` → `File.Replace(tmp, path, .bak)` (with a
`PlatformNotSupportedException` fallback to copy-then-move). A crash mid-write can
never leave a half-written or missing wallet; the previous contents are always
recoverable from `agent-identity.wallet.bak`.

### 3.5 Provenance

`WalletCrypto`, `UnlockThrottle`, the atomic-save pattern, `IPinStore` and
`DpapiPinStore` were copied from the now-retired `Svrn7.Trust.KeyWallet` (which
was ECDSA P-256, single-key). AgentWallet generalised the payload from "one
PKCS#8 key" to a JSON document and is now the sole holder of that code. The
key-type-specific parts (`KeyPair`, `EcMath`, `Mnemonic`) were **not** reused —
key generation is `Svrn7.Crypto`/NBitcoin/NSec, recovery is NBitcoin BIP39/BIP32.

---

## 4. The password

### 4.1 How it is obtained (`WalletPasswordPrompt`)

1. **`PANDO_WALLET_PASSWORD`** environment variable, if set → used as-is, no
   confirmation.
2. Otherwise an **interactive prompt**; on first-run wallet *creation* it is
   entered twice and the two must match.
3. If stdin is **not a TTY** (detached process, service, redirected) **and** the
   environment variable is absent → the TDA **fails fast** with a clear message.
   It never blocks on a prompt that cannot be answered.

There is no `--password` flag and no `--password-file` — the environment variable
is the only non-interactive source.

### 4.2 Handling in memory

- Passwords are `char[]`, never `string` (strings are immutable and may be copied
  by the GC, so they cannot be reliably wiped).
- The caller owns the array and clears it; `AgentWalletService.Unlock` clears the
  array its provider returns.
- The Argon2id-derived key and the decrypted payload bytes are zeroed
  (`CryptographicOperations.ZeroMemory`) as soon as they are no longer needed.

### 4.3 Trade-off

An environment-variable password is readable by other processes running as the
**same OS user** for the lifetime of the TDA process — `/proc/<pid>/environ` and
`ps eww` on Linux, `ReadProcessMemory` on Windows. The interactive prompt keeps
the secret out of the process environment block entirely, so it is strictly the
more private option; the environment variable is the automation escape hatch.
"Env if set, else prompt" gives humans the private path and scripts the
non-interactive one.

### 4.4 Changing the password

`AgentWalletService.ChangePassword` re-derives the Argon2id key from the new
password, re-encrypts the payload, and rewrites `agent-identity.wallet` (atomic,
new `.bak`). **The databases are never re-keyed** — see §6.

---

## 5. Unlock throttling (`UnlockThrottle`)

Slows repeated wrong-password guesses made **through this process's own unlock
path**. State is a small JSON sidecar, `agent-identity.wallet.lockout`, so it
survives process restarts (throttling applies at every startup, including a
supervised auto-restart with a bad secret — that escalating backoff is the
intended signal).

| Failed attempts | Wait before the next attempt |
|---|---|
| 1–2 | none (free) |
| ≥ 3 | `min(300, 2^(failures − 2))` seconds — exponential, **capped at 300 s** |

- A successful unlock, or any fresh write to the wallet, resets the counter.
- A **corrupted** sidecar fails **open** — "no recorded failures" — because a
  broken non-secret file must never lock a user out of their own wallet.
- **Scope:** this is defense-in-depth for *interactive* guessing only. Someone
  who copies `agent-identity.wallet` elsewhere and attacks it offline bypasses
  the throttle entirely — the control there is the Argon2id cost (§3.3), not
  this class.

---

## 6. Encrypted LiteDB databases

### 6.1 What is encrypted

All five databases, plus their write-ahead logs:

```
svrn7.db  svrn7-dids.db  svrn7-msg.db  svrn7-vcs.db  svrn7-schemas.db
svrn7-log.db  svrn7-dids-log.db  svrn7-msg-log.db  svrn7-vcs-log.db  svrn7-schemas-log.db
```

The set of DIDs an instance knows, its message bodies, its verifiable
credentials, its schema registry, its citizen/society membership — all of it is
AES-encrypted at rest even though individual DID Documents are themselves
publishable.

### 6.2 How

`Svrn7Options.DatabasePassword` (inherited by `Svrn7SocietyOptions`) carries the
hex of the wallet's 32-byte **DB master key**. When set, every context opens its
file as:

```
Filename="<path>";Password=<64 hex chars>
```

LiteDB 5 then applies its native encryption: AES with a PBKDF2-derived key, salt
stored in a reserved header page, transparent per-8 KB-page. `DbConnectionString`
in [ISvrn7Driver.cs](src/Svrn7.Federation/ISvrn7Driver.cs) builds the string; the
context classes (`Svrn7LiteContext`, `DidRegistryLiteContext`, `MsgLiteContext`,
`SchemaLiteContext`, …) receive it unchanged. Tests and standalone tooling that
pass a bare path with no password get **cleartext**, unchanged.

### 6.3 Why the DB key is a stable random value, not password-derived

`dbMaster` is 32 random bytes generated **once**, at wallet creation, and stored
as `dbMasterKeyHex` inside the sealed payload. It is **not** derived from the
password. Therefore:

- A **password change** re-seals the wallet (cheap) but leaves `dbMaster`
  unchanged → **no database is ever re-keyed** on a password change.
- Deriving the DB password directly from the wallet password would instead force
  a full LiteDB `Rebuild` of all five files every time the password changed.

### 6.4 Rotating the DB key (compromise response only)

`AgentWalletService.RotateDatabaseKey` generates a fresh `dbMaster`, rewrites the
wallet, and returns `{ OldKey, NewKey }`. The caller must then `Rebuild` each
database (open with `OldKey`, rebuild with `NewKey`). This is an explicit, rare
operation for a suspected key-compromise event — not part of normal password
maintenance.

### 6.5 Consequences

- **No cleartext inspection.** LiteDB Studio and the `litedb` CLI cannot open the
  files without the password. The sanctioned inspection path is
  **`Svrn7.TDA.dll db-shell`** — it unlocks the wallet, derives the DB password,
  opens a chosen `mem/*.db`, and lists collections / dumps documents.
- **Exclusive lock.** LiteDB takes an exclusive lock on an encrypted file, which
  doubles as the guard against two TDA processes double-mounting one identity
  (the second fails to open the databases).
- **Overhead (estimate, not benchmarked in-repo):** ~1 fixed header page of
  storage; roughly 10–15 % CPU/latency on mixed read/write with AES-NI, higher on
  cold-cache scans (every page fault also decrypts), near-zero on cache-hot
  reads. The DID-resolve path runs on every inbound reply-routing — measure with
  representative data before relying on the figure.

---

## 7. The recovery phrase

- **12-word BIP39** mnemonic, 128-bit entropy, generated with `NBitcoin.Mnemonic`
  (a standard, well-tested implementation). `bip39EntropyBits` in the payload
  records the length so a later move to 24 words / 256 bits is non-breaking.
- The **secp256k1 identity key** is derived via BIP32 at the Web7-owned path
  **`m/7'/0'/0'/0/0`** (purpose `7'` — deliberately not SLIP-0044-registered).
- The **X25519 key-agreement key** is re-derived from the *same* BIP39 seed:
  `HKDF-SHA256(ikm = seed, salt = "", info = "web7-pando/x25519/v1", L = 32)`,
  then RFC 7748-clamped. One phrase therefore restores **both** keys.
- Because the DID genesis hash is `Blake3(secp256k1 pubkey)`, the phrase restores
  the entire DID identity.
- The phrase is stored **inside the encrypted payload** (equally secret; no
  reconstruction step). `AgentWalletService.ExportRecoveryPhrase` returns it
  after a successful unlock; a wallet created from raw keys with no phrase
  returns `null`.
- **Caveat:** the path/derivation is Web7-internal. A phrase generated here
  recovers the identity **in this tooling only** — it will not import into a
  standard BIP32/44 wallet.
- **First-run:** `Create` with no `--recovery-phrase` generates a phrase and
  prints it once on the startup banner ("write this down now, shown only once").
  `--recovery-phrase "<12 words>"` restores an existing identity under a new
  password.

---

## 8. Public-key pinning — trust on first use

`agent-identity.wallet` carries its own `secp256k1PublicKeyHex` in the cleartext
header, but that value is *self-asserted*: an attacker who drops in their own
wallet file supplies a matching public key in the same file. A **pin** is an
independent second copy of the expected public-key hash, kept somewhere a plain
file swap cannot reach.

| Aspect | Detail |
|---|---|
| Pin value | `SHA-256(secp256k1 compressed public key bytes)` |
| Store key (`walletId`) | the wallet's absolute path (so moving/renaming reads as a new wallet) |
| Windows store | `DpapiPinStore` — DPAPI at `CurrentUser` scope, app-specific entropy `"Svrn7.Trust.AgentWallet.PinStore.v1"`, at `%LOCALAPPDATA%\Web7Pando\AgentWallet\pin-store.bin` — **deliberately not** next to the wallet |
| Non-Windows | `NullPinStore` — pins nothing, every check reads as "first use" (no portable OS keystore wired up yet) |

**On unlock:**

- **`PinCheck.Mismatch`** → refuse **before** prompting for a password
  (`AgentUnlockResult.PinMismatch`).
- **`PinCheck.FirstUse`** + an enabled store → after a *successful* password
  check, enrol the wallet's key as the pin (trust on first use).
- **`PinCheck.Match`** → proceed silently.

**Fail-open, loudly:** if the pin file cannot be decrypted (corrupted, or written
by a different Windows user) the store surfaces the error and the session
continues with pinning **disabled** — the pin file holds only public-key hashes,
so a broken one must never brick the TDA. This mirrors the throttle sidecar's
stance.

**Scope:** pinning defends against dropping a foreign wallet file into place
*without* code execution as the enrolling user. It does **not** defend against a
process already running as that user (which can re-seal its own pin, and can
keylog).

---

## 9. Key material in memory

| `TdaOptions` field | Key | Use | Source |
|---|---|---|---|
| `AgentSigningPrivateKey` | secp256k1, 32 bytes | outbound DIDComm JWS (`DIDCommMessageSwitchboard.PackOutboundAsync`), transaction signing | wallet payload |
| `AgentKeyAgreementPrivateKey` | X25519, 32 bytes | inbound JWE decryption (`KestrelListenerService.UnpackAsync`) | wallet payload |
| `DatabaseMasterKey` | random, 32 bytes | LiteDB `Password=` (hex) for all five databases | wallet payload |

- All three are decrypted from the wallet **before the host is built** and held
  for the process lifetime.
- **None ever enters a LOBE PowerShell runspace.** `Svrn7RunspaceContext` (the
  `$SVRN7` object injected into every runspace) exposes the driver, inbox, cache,
  epoch — never raw key bytes. A buggy or malicious LOBE cannot read them.
- The password `char[]` and the Argon2id-derived key are zeroed immediately after
  the unlock; the decrypted payload bytes are zeroed once the fields are copied
  out. The three long-lived key arrays are not zeroed until process exit (they
  are needed for the whole run).

---

## 10. At-rest secret cache — `ISecretProtector`

An **optional** convenience so a supervised restart need not re-prompt. Never
required for operation.

```csharp
interface ISecretProtector { bool Enabled { get; } byte[] Protect(byte[] p); byte[] Unprotect(byte[] s); }
```

| Platform | Implementation |
|---|---|
| Windows | `DpapiSecretProtector` — `ProtectedData`, `CurrentUser` scope, entropy `"Svrn7.Trust.AgentWallet.SecretProtector.v1"` |
| everything else | `NullSecretProtector` — `Enabled == false`; the methods throw if called anyway, so a caller that ignored `Enabled` fails loudly instead of storing plaintext |

Deferred behind the seam: macOS/iOS Keychain, Linux `systemd-creds` /
`LoadCredentialEncrypted=`, libsecret / Secret Service, kernel keyring.

---

## 11. Instance naming, port, and isolation design

### 11.1 The bootstrap circularity

Two things want to key an instance's storage: the **DID** (the identity) and the
**listen port** (the network endpoint). Neither is available at the moment it is
needed:

- The DID is derived from the genesis key pair (`Blake3(secp256k1 pubkey)`), and
  the key pair lives *inside* the encrypted wallet, which lives *inside* the
  directory you are trying to name.
- The port is written into the DID Document `serviceEndpoint`, which lives inside
  an encrypted database, which lives inside the same directory.

The design breaks both loops by **deriving identity first, before the host is
built or anything is persisted**:

1. Parse `--name` (and optional `--did`).
2. Resolve the data root (`--data-root` › `$PANDO_HOME` › `~/.web7-pando`).
3. **Locate** an existing instance by scanning `~/.web7-pando/*/identity.meta.json`.
4. If none: generate the key pair in memory, compute `genesisHash`, and only then
   name the directory.
5. Bind the port (atomically — §11.3) *before* the DID Document is written.

### 11.2 Directory name — why `<name>-<genesisHash8>`

Options considered:

| Candidate | Verdict |
|---|---|
| **The port** (`<BaseDir>/{port}/mem/`, the old scheme) | Rejected. The port is a network detail, not an identity; the same identity on a different port became a different data set. |
| **The full DID** | Rejected. Contains `:` and `/` (illegal / awkward on Windows) and hits the 260-char path ceiling. Also *changes format* on a role transition (`wanderer.svrn7.net/agent/…` → `<society>.svrn7.net/citizen/…`). |
| **A central `instances.json` index** mapping name → folder | Rejected. A single mutable file is a corruption point, needs write-locking for concurrent instance creation, and drifts from what is actually on disk. |
| **`<name>-<genesisHash[..8]>`** (chosen) | Human-scannable in a directory listing **and** correlated with the DID. |

`genesisHash = Blake3(secp256k1 compressed public key)`, hex, first 8 chars. Key
properties:

- **Not secret** — it is a hash of a public key. Safe as a directory name, safe
  in logs.
- **Stable across role transitions.** A TDA is promoted Wanderer → Citizen →
  Society/Federation *in place*; the DID string format changes but the key pair —
  and therefore the genesis hash — does not. One directory = one key pair = one
  genesis hash = possibly several role-DIDs over its lifetime.
- **8 hex chars = 32 bits** of collision resistance within one machine's handful
  of instances — adequate; a collision is detected at creation and the slug
  lengthened.

### 11.3 Instance discovery — directory scan, no index

Each instance directory carries `identity.meta.json` (cleartext, non-secret).
It is kept minimal: `did` and `name` are the startup selectors; `parentTdaDid`
(added by `SetParentTda` after a Society/Federation registration) is an **opaque
pointer** to the parent identity. `role` and `createdUtc` are not read by code —
they are there only so `cat identity.meta.json` tells a human what the instance
is. Startup enumerates these files and matches on `--name` (or `--did`), then
**rewrites the file unconditionally** (`IdentityMeta.TryLoad` drops unknown JSON
keys, so a file from an older build is scrubbed of `serviceEndpointUrl` /
`secp256k1PublicKeyHex` on the next launch). Properties:

- **Self-healing** — drop a directory in, it appears; delete one, it is gone.
- **No locking** — reads only; concurrent first-runs each create their own
  directory and cannot collide on a shared registry.
- `n` is tiny (a handful of TDAs per machine), so the O(n) scan is free.
- `identity.meta.json` is the *only* cleartext record of an identity now that the
  DID Document lives inside an encrypted database. It is unauthenticated (§13).

**One secure source for every endpoint URL.** `identity.meta.json` deliberately
carries **no endpoint** — not this identity's, not its parent's. Every endpoint
is read from the **encrypted** `svrn7-dids.db`:

- *This identity's own bound port* — `DidRegistryPeek.TryReadServiceEndpoint`
  opens `svrn7-dids.db` read-only in the pre-host block (the wallet is unlocked
  by then, so the DB master key is in hand), pulls the `DIDCommMessaging`
  service-endpoint URL from this identity's own DID Document, and binds exactly
  that port.
- *The parent-tier endpoint* — resolved after `Host.Build()` from the parent's
  DID Document via `parentTdaDid`, held in memory only.

Consequence: a local attacker who edits `identity.meta.json` can at worst make a
lookup fail (DoS) — flipping `parentTdaDid` breaks the parent resolution, and it
is re-derived cleanly on the next good startup. They **cannot** move the
listener or point DID-resolution escalation at an endpoint they control, because
no cleartext file feeds an endpoint into the process. (The plaintext
`did-resolve-response` is unsigned, so a redirected escalation target would be a
full MITM primitive — hence the hard rule that endpoints come only from the
encrypted registry.)

### 11.4 Port — auto-select once, then fixed

**First run only**, the listen port is chosen: `--port` if given verbatim,
otherwise auto-selected upward from `--port-base` (default 8440) within
`--port-span` (default 64). The chosen port is written into the DID Document
`serviceEndpoint` inside the encrypted `svrn7-dids.db` — the single authoritative
record; nothing mirrors it in cleartext.

**Every later run** reads that endpoint back with `DidRegistryPeek` (§11.3) and
binds *exactly* the published port. A conflicting `--port` is **rejected**, not
silently honoured, because other components across the ecosystem cache DID
Documents — rewriting a published `serviceEndpoint` breaks every cached copy
until it re-resolves. Moving the endpoint is a deliberate, separate gesture:
`--republish-endpoint` rewrites the DID Document (`Version` + 1), with a printed
cache-staleness notice.

### 11.5 The port claim is atomic

The socket is **bound and put into listening state in the pre-host block, and
held for the process lifetime**, then handed to Kestrel via
`kestrel.ListenHandle(...)`. Binding here — before the host is built and before
the DID Document is written — means:

- **The claim is atomic.** The socket is never released between "chosen" and
  "serving", so two TDAs first-running concurrently cannot both take the same
  port (the loser of 8440 advances to 8441, etc.). Contrast a *probe-then-bind*
  approach, which has a TOCTOU gap.
- The port is known before anything is persisted, so no post-bind patch of the
  DID Document is needed on the common path.

### 11.6 Isolation and destructive operations

- **Double-mount guard.** LiteDB's exclusive lock on the encrypted databases
  fails the second process that points at the same identity directory.
- **`--reset`** deletes the **entire** instance directory (wallet, `.bak`,
  `.lockout`, meta, `lobes/`, `mem/`) and re-bootstraps. It prompts for
  confirmation on a terminal; a non-interactive run proceeds (for testnet
  scripts). There is no migration path from the old `<BaseDir>/{port}/mem/`
  layout — `--reset` is the only transition.

---

## 12. Cryptographic primitives inventory

| Primitive | Library | Where |
|---|---|---|
| Argon2id | `Konscious.Security.Cryptography.Argon2` | wallet password KDF |
| AES-256-GCM | `System.Security.Cryptography.AesGcm` (BCL) | wallet payload seal |
| AES (per-page) | LiteDB 5 built-in | database encryption |
| SHA-256 | BCL | public-key pin |
| HKDF-SHA256 | `System.Security.Cryptography.HKDF` (BCL) | X25519 key from the BIP39 seed |
| Blake3 | `Blake3` | DID genesis hash |
| secp256k1 (keygen, ECDSA/ES256K, BIP32/BIP39) | `NBitcoin` | identity key, recovery |
| X25519 | `NSec.Cryptography` (libsodium) | key-agreement key |
| DPAPI | `System.Security.Cryptography.ProtectedData` | pin store, secret cache (Windows only) |

---

## 13. Local threat model

### Protects against

- **Stolen disk image / backup / lost laptop.** Without the password, the wallet
  blob and all five databases are AES-encrypted; Argon2id (64 MiB, 3 passes)
  makes an offline password attack expensive.
- **Foreign wallet-file swap.** Dropping someone else's `agent-identity.wallet`
  into place is caught by the public-key pin (Windows) before any password is
  entered.
- **A buggy or hostile LOBE reading key material.** Private keys and the DB key
  are never placed in a runspace; `$SVRN7` exposes no raw key bytes.
- **Casual inspection.** No cleartext key file, no cleartext database; the DB key
  is only reachable through a wallet unlock.

### Does NOT protect against

- **Code running as the same OS user.** It can read the TDA's process memory,
  keylog the password prompt, read `PANDO_WALLET_PASSWORD` from the environment,
  and re-seal its own pin. Pinning and the throttle explicitly do not change
  this.
- **Offline brute force beyond the KDF cost.** The unlock throttle only slows
  guessing through the running TDA; a copied wallet is bounded solely by
  Argon2id.
- **A weak or reused password.** The KDF raises the cost per guess; it cannot
  rescue a guessable password. There is no server-side lockout.
- **Supply chain of LOBE packages.** `.nupkg` files in `lobe-library/` are
  extracted and their PowerShell modules run in-process; there is **no signature
  verification** on LOBE packages today. Populate `lobe-library/` (and any
  `--lobe-feed`) only from sources you trust. (Tracked as a limitation, not yet
  addressed.)
- **`identity.meta.json` tampering.** It is cleartext and unauthenticated. It
  holds nothing security-sensitive — no keys, **no endpoint URLs** (§11.3) — so
  the worst a local edit achieves is a denial of service on the instance lookup
  or the parent resolution, both of which recover on the next good startup. All
  endpoints come from the encrypted `svrn7-dids.db`; the wallet and keys are
  untouched.

---

## 14. Operator checklist

- Set a strong, unique `PANDO_WALLET_PASSWORD`, or use the interactive prompt.
- **Record the 12-word recovery phrase** shown once at first run — it is the only
  way back if the password is lost.
- Back up `agent-identity.wallet` (and `.bak`); losing it with no recovery phrase
  is unrecoverable.
- Keep `~/.web7-pando/lobe-library/` (and `--lobe-feed`) sourced from trusted
  builds — LOBE code is not signature-checked.
- To inspect databases: `dotnet Svrn7.TDA.dll db-shell --name <instance>`.
- To rotate the password: `AgentWalletService.ChangePassword` (no DB rebuild).
- To move a published endpoint: `--republish-endpoint` (rewrites the DID Document
  in the encrypted `svrn7-dids.db`, `Version` + 1; cached resolvers lag until
  they re-resolve).
