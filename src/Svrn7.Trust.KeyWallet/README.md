# Svrn7.Trust.KeyWallet

A small, dependency-light .NET library for a **locally-stored, password-protected
key pair**: an ECDSA P-256 private key encrypted at rest, unlocked with a
password, optionally backed by a BIP39-style recovery phrase, with public-key
pinning, unlock throttling, and OpenTelemetry-ready diagnostics.

It is **UI-agnostic**. Every operation returns data or a result object; nothing
here writes to a console, reads the environment for prompts, or assumes an
interactive session. The `KeyWallet` console app in this repo is a reference
host built entirely on the public API below.

- **Target framework:** `net8.0`
- **Dependencies:** [`Konscious.Security.Cryptography.Argon2`](https://www.nuget.org/packages/Konscious.Security.Cryptography.Argon2) `1.3.1`,
  [`System.Security.Cryptography.ProtectedData`](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData) `8.0.0` (Windows DPAPI; used only by `DpapiPinStore`)
- **Threat model:** [`doc/THREAT_MODEL.md`](doc/THREAT_MODEL.md) — read it before relying on this for anything real.

---

## Table of contents

- [Install](#install)
- [Quick start](#quick-start)
- [What's on disk](#whats-on-disk)
- [The `WalletService` facade](#the-walletservice-facade)
- [Recovery phrases](#recovery-phrases)
- [Public-key pinning](#public-key-pinning)
- [Unlock throttling](#unlock-throttling)
- [Working with keys directly](#working-with-keys-directly)
- [Secret handling contract](#secret-handling-contract)
- [Diagnostics](#diagnostics)
- [Platform notes](#platform-notes)
- [Building and testing](#building-and-testing)
- [API surface at a glance](#api-surface-at-a-glance)

---

## Install

There is no published NuGet package yet. Reference the project directly:

```xml
<ProjectReference Include="..\Svrn7.Trust.KeyWallet\Svrn7.Trust.KeyWallet.csproj" />
```

or, once packed (`dotnet pack -c Release`):

```xml
<PackageReference Include="Svrn7.Trust.KeyWallet" Version="1.0.0" />
```

All public types live in the `Svrn7.Trust.KeyWallet` namespace.

---

## Quick start

```csharp
using Svrn7.Trust.KeyWallet;

// Pick a pin store for this machine (DPAPI on Windows, no-op elsewhere).
var (pinStore, unavailableReason) = PinStores.CreateDefault();
if (unavailableReason is not null)
    Console.Error.WriteLine($"Pinning disabled: {unavailableReason}");

var wallet = new WalletService("wallet.json", pinStore);

// --- Create -----------------------------------------------------------------
char[] password = "correct horse battery staple".ToCharArray();
WalletWriteResult created = wallet.Create(password);
Array.Clear(password);
Console.WriteLine($"Public key: {created.PublicKeyBase64}");
created.KeyPair.Dispose();               // option: keep it if you want it unlocked now

// --- Unlock ---------------------------------------------------------------
UnlockResult result = wallet.Unlock(() => PromptForPassword());   // provider is
                                                                 // only called if
                                                                 // not throttled /
                                                                 // pin-mismatched
switch (result)
{
    case UnlockResult.Success s:
        using (s.KeyPair)
        {
            byte[] sig = s.KeyPair.Sign(Encoding.UTF8.GetBytes("hello"));
            // ...
        }
        break;

    case UnlockResult.WrongPassword:
        Console.WriteLine("Wrong password (a throttle failure was recorded).");
        break;

    case UnlockResult.Throttled t:
        Console.WriteLine($"Locked out for another {t.RetryAfter}.");
        break;

    case UnlockResult.PinMismatch m:
        Console.WriteLine("Refused: wallet file does not match its pinned key.");
        break;

    case UnlockResult.NoWalletFile:
        Console.WriteLine("No wallet yet — create one.");
        break;
}
```

`WalletService` is cheap to construct and bound to one wallet path. It is **not
thread-safe**; serialize access yourself if more than one thread can touch the
same wallet.

---

## What's on disk

### `wallet.json` — the wallet file

Plain JSON. The private key is **only** ever stored encrypted.

```jsonc
{
  "Version": 2,
  "PublicKeyBase64": "<SubjectPublicKeyInfo DER, base64>",
  "EncryptedPrivateKeyBase64": "<see below>",
  "CreatedUtc": "2026-09-01T12:34:56.789Z"
}
```

`WalletFile.Save` writes **atomically**: it serialises to `wallet.json.tmp`,
flushes to disk, then swaps it into place with `File.Replace`, preserving the
previous contents at `wallet.json.bak`. A crash mid-write can never leave a
half-written or missing wallet.

### Encryption formats

| `Version` | KDF | Blob layout (before base64) |
|-----------|-----|----------------------------|
| **1** (legacy, still readable) | PBKDF2-HMAC-SHA256, 600 000 iterations | `salt(16) ‖ nonce(12) ‖ tag(16) ‖ ciphertext` |
| **2** (current, written by `WalletFile.Create`) | Argon2id, 64 MiB / 3 passes / parallelism 4 | `memKiB(4) ‖ iters(4) ‖ par(4) ‖ salt(16) ‖ nonce(12) ‖ tag(16) ‖ ciphertext` |

Both use **AES-256-GCM** for the actual encryption; the GCM auth tag is what
makes a wrong password or a tampered blob fail loudly (`CryptographicException`)
rather than returning garbage. Version 2 embeds its Argon2 cost parameters in
the blob, so the cost can be raised later without a format version bump. Old
Version 1 files keep working untouched until they are re-encrypted (e.g. by a
password change), which transparently upgrades them to Version 2.

### Sidecar files

- `wallet.json.lockout` — JSON, unencrypted. `UnlockThrottle` state (failed
  attempt count + last-failure timestamp). Deleted on a successful unlock or a
  fresh write. A corrupted one fails **open** (treated as "no failures").
- `wallet.json.bak` — the previous wallet file, kept by the atomic save.

The pin store does **not** live next to the wallet — see
[Public-key pinning](#public-key-pinning).

---

## The `WalletService` facade

`WalletService` composes the lower-level primitives (`WalletFile`,
`WalletCrypto`, `Mnemonic`, `IPinStore`, `UnlockThrottle`,
`KeyWalletDiagnostics`) into the full operations, applying throttling, pinning +
trust-on-first-use, and instrumentation **in the order those steps must
happen**. Reimplementing this ordering by hand is where security bugs get
introduced (enrolling a pin before the password is checked; forgetting to record
a throttle failure), so prefer the facade.

| Method | Does |
|--------|------|
| `Create(char[] password)` | Generate a random key, encrypt, write, reset throttle, pin. Returns the key **unlocked** — dispose it (immediately if you only wanted the file). |
| `Unlock(Func<char[]> passwordProvider)` | Throttle check → pin check → *then* call the provider for the password → decrypt → reset throttle → enrol pin on first use. Returns an [`UnlockResult`](#unlockresult). The `char[]` the provider returns is zeroed before the method returns. |
| `Save(KeyPair key, char[] password, bool fromRecoveryPhrase = false)` | Re-encrypt an already-held key under a (new) password and write it. Used for "change password" and for persisting a key from `DeriveFromRecoveryPhrase`. **Does not** dispose `key`. |
| `TryInspect()` | Read-only: the file's public key + its `PinCheck` status, or `null` if there is no file. Never refuses on a mismatch — a read decides for itself. |
| `DeriveFromRecoveryPhrase(string mnemonic)` | Validate + derive the key, no disk, no pin store. For "show the phrase / confirm before persisting" flows. Throws `FormatException` on a bad phrase. |
| `CreateFromRecoveryPhrase(string mnemonic, char[] password)` | `DeriveFromRecoveryPhrase` + `Save(..., fromRecoveryPhrase: true)` in one call. |
| `static GenerateRecoveryPhrase()` | A fresh 12-word phrase. Not persisted anywhere. |
| `WalletFileExists` / `PinStore` | The obvious accessors. |

### `UnlockResult`

A closed record hierarchy — `switch` on it exhaustively:

| Case | Meaning | Password read? |
|------|---------|----------------|
| `Success(KeyPair KeyPair, bool PinnedOnFirstUse)` | Unlocked. Caller owns & disposes `KeyPair`. | yes |
| `NoWalletFile(string Path)` | Nothing at the wallet path. | no |
| `Throttled(TimeSpan RetryAfter)` | Too many recent failures. | no |
| `PinMismatch(byte[] PinnedHash, byte[] ActualHash)` | File's key ≠ pinned key; file replaced or rolled back. Hashes are SHA-256 of the SPKI public key. | no |
| `WrongPassword` | Bad password *or* tampered ciphertext (indistinguishable). A throttle failure was recorded. | yes |

Other errors (unsupported file version, malformed base64, IO errors) surface as
exceptions, not result cases.

### `WalletWriteResult`

`(KeyPair KeyPair, string PublicKeyBase64, bool PublicKeyPinned)` — returned by
`Create`, `CreateFromRecoveryPhrase`, and `Save`. For a plain `Save` call
`KeyPair` is the very instance you passed in.

---

## Recovery phrases

`Mnemonic` implements the standard **BIP39 mnemonic ⇄ seed** steps: 12 words /
128-bit entropy with a checksum, and PBKDF2-HMAC-SHA512, 2048 iterations,
`"mnemonic"` + passphrase salt, 64-byte output.

> **Scope note.** How that 64-byte seed becomes a P-256 private key
> (`KeyPair.FromSeed`, via `EcMath`) is **this wallet's own construction**, not a
> published standard — BIP32/44 HD derivation is defined for secp256k1, not
> P-256. A phrase generated here recovers the key **in this library only**; it
> will not import into a standard BIP32/44 wallet.

`KeyPair.FromSeed` needs to compute `Q = d·G` itself (the BCL's `ECDsa` only
generates random keys). `EcMath` does that with a Montgomery-ladder scalar
multiply written from the published P-256 parameters. It is a **timing-hardened
mitigation, not a certified constant-time implementation** (`BigInteger`
underneath is not constant-time). `EcMath.SelfTest()` cross-checks it against the
BCL for freshly generated keys — a host that offers seed derivation should run it
at startup and disable phrase operations if it ever returns `false`.

```csharp
if (!EcMath.SelfTest())
    // do not trust FromSeed-derived keys on this runtime
```

---

## Public-key pinning

`wallet.json` carries its own `PublicKeyBase64`, but that is *self-asserted*: an
attacker who drops in their own `wallet.json` supplies a matching public key in
the same file. A **pin** is an independent second copy of the expected
public-key hash, kept somewhere a plain file swap cannot reach.

```
IPinStore
├─ DpapiPinStore     Windows. DPAPI (CurrentUser) at
│                    %LOCALAPPDATA%\KeyWallet\pin-store.bin — deliberately not
│                    next to wallet.json. Fails loudly (throws) if the file
│                    can't be decrypted.
├─ InMemoryPinStore  Non-persistent. Tests, or hosts managing their own storage.
└─ NullPinStore      Pins nothing; every check reads as "first use". Used on
                     non-Windows and as the fail-open fallback.
```

`PinStores.CreateDefault()` picks one for the current OS and **fails open**: if
the Windows store exists but can't be opened (most often a pin file written by a
different Windows user) you get a `NullPinStore` plus a non-null
`UnavailableReason` string — never an exception. The pin file holds only
public-key hashes, so a broken one must never lock a user out of their wallet.

On unlock, `WalletService`:

- **`PinCheck.Mismatch`** → refuses before prompting for a password
  (`UnlockResult.PinMismatch`).
- **`PinCheck.FirstUse`** + enabled store → after a *successful* password check,
  enrols the wallet's key as the pin (**trust on first use**;
  `Success.PinnedOnFirstUse == true`).
- **`PinCheck.Match`** → proceeds silently.

Every write (`Create`, `Save`, …) re-asserts the pin, so a pre-pinning wallet
gets enrolled the first time it is written on a pinning-capable machine.

`walletId` (the pin store key) defaults to the wallet's **absolute path**, so
moving or renaming the file reads as a new wallet. Pass an explicit stable id to
`WalletService` if the file can legitimately move.

### One shared pin file per user

`DpapiPinStore.DefaultPath` is a fixed location —
`%LOCALAPPDATA%\KeyWallet\pin-store.bin` — and the DPAPI entropy is a library
constant. So **every app built on this library that takes the defaults shares
one pin file per Windows user.** That file is a map of `walletId → public-key
hash`, so multiple wallets (and multiple apps) coexist in it without colliding,
as long as their `walletId`s differ — which, with the default, means their
wallet files resolve to different absolute paths. Two apps pointed at the *same*
wallet file correctly share its pin.

The one footgun: two *different* apps that both use a relative `wallet.json` and
are ever launched from the same working directory resolve to the same absolute
`walletId` and would share a pin unintentionally. To isolate an app completely,
override both knobs:

```csharp
var store  = new DpapiPinStore(Path.Combine(myAppLocalAppData, "pins.bin"));
var wallet = new WalletService(walletPath, store, walletId: "my-app:main");
```

DPAPI is `CurrentUser` scope: any process running as that user can decrypt the
file. The entropy only stops an unrelated app's blanket "unprotect everything"
sweep from reading it; it is **not** an isolation boundary between apps.

**Out of scope:** pinning does not defend against code already running as the
enrolling user (it can re-seal its own pin, and can keylog). See the threat
model.

---

## Unlock throttling

`UnlockThrottle` slows repeated wrong-password guesses **made through this
library's unlock path**. State is a small JSON sidecar (`wallet.json.lockout`)
so it survives process restarts.

- First **2** wrong guesses are free.
- After that: exponential backoff, `1 << (failures - 2)` seconds, **capped at
  300 s**. A bounded wait, not a hard lockout — a permanent lockout on a
  single-user local wallet just turns a typo streak into self-inflicted denial
  of service.
- A successful unlock, or any fresh write, calls `UnlockThrottle.Reset`.

This is defense-in-depth for *interactive* guessing only. Someone who copies
`wallet.json` elsewhere and attacks it offline bypasses it entirely — the real
control there is the KDF cost (Argon2id), not this.

---

## Working with keys directly

`KeyPair` is the only algorithm-specific type; swapping curves would touch it and
nothing else.

```csharp
using KeyPair kp = KeyPair.Generate();               // random P-256 key
byte[] sig = kp.Sign(data);                           // ECDSA / SHA-256
bool ok  = KeyPair.Verify(kp.PublicKeySubjectPublicKeyInfo, data, sig);   // static
string pub = kp.PublicKeyBase64;                      // SPKI DER, base64

using KeyPair fromSeed = KeyPair.FromSeed(seed64);    // deterministic (see above)
using KeyPair imported = KeyPair.FromPrivateKey(pkcs8Bytes);
```

`KeyPair` holds the PKCS#8 private key bytes in memory. **`Dispose()` zeroes
them**; after that `Sign` throws `ObjectDisposedException`. `KeyPairSession` is a
small helper for hosts that keep exactly one key unlocked at a time:

```csharp
using var session = new KeyPairSession();
session.Replace(unlocked);   // disposes whatever it held before, unless it's the same instance
session.Current?.Sign(...);
session.IsUnlocked;          // bool
session.Lock();              // dispose + clear
```

---

## Secret handling contract

- **Passwords are `char[]`, never `string`.** Strings are immutable and may be
  copied by the GC, so they cannot be reliably wiped. Pass `char[]`; the caller
  still owns it and should `Array.Clear` it once the call returns.
  `WalletService.Unlock` clears the array returned by its password provider for
  you.
- **Derived key material, seeds, and decrypted private key bytes are zeroed**
  (`CryptographicOperations.ZeroMemory` / `Array.Clear`) as soon as they are no
  longer needed, inside the library.
- **`KeyPair` instances are owned by whoever receives them.** `UnlockResult.Success`,
  `WalletWriteResult`, `Generate`, `FromSeed`, `FromPrivateKey`, and
  `DeriveFromRecoveryPhrase` all hand you a key you must `Dispose`.
  `WalletService.Save` is the exception — it borrows the key you pass and does
  not dispose it.
- **No secret is ever put on a diagnostics tag** — see below.

---

## Diagnostics

`KeyWalletDiagnostics` defines **one `ActivitySource` and one `Meter`, both named
`"KeyWallet"`**, and the instruments on them. It attaches **no exporter**. With
no listener wired up (the default for a standalone run), `StartActivity` returns
`null` and every `Add`/`Record` is a no-op, so the cost is negligible.

A host opts in by pointing its own OpenTelemetry pipeline at the source names:

```csharp
sdkBuilder
    .WithTracing(t => t.AddSource("KeyWallet"))
    .WithMetrics(m => m.AddMeter("KeyWallet"));
```

| Instrument | Kind | Tags |
|------------|------|------|
| `keywallet.wallets_created` | Counter | `keywallet.with_recovery_phrase` |
| `keywallet.unlock.total` | Counter | `keywallet.result` = `success` \| `wrong_password` \| `throttled` \| `pin_mismatch` |
| `keywallet.unlock.kdf.duration` | Histogram (ms) | `keywallet.kdf` = `pbkdf2` \| `argon2id`, `keywallet.result` |
| `keywallet.sign.total` | Counter | `keywallet.result` = `success` \| `locked` \| `error` |
| `keywallet.pin.checks` | Counter | `keywallet.pin.result` = `match` \| `first_use` \| `mismatch` |

Spans: `KeyWallet.Unlock` (created inside `WalletService.Unlock`) and
`KeyWallet.Sign` (create it yourself around a `KeyPair.Sign` call — see the
console app). Record results with the helpers so tag/status stay consistent:

```csharp
using var activity = KeyWalletDiagnostics.ActivitySource.StartActivity("KeyWallet.Sign");
try   { /* sign */ KeyWalletDiagnostics.RecordSignResult(activity, KeyWalletResult.Success); }
catch { KeyWalletDiagnostics.RecordSignResult(activity, KeyWalletResult.Error); throw; }
```

**Attribute hygiene (enforced by convention):** tags only ever carry the keys
and the low-cardinality enum-like values in `KeyWalletResult` / the `Tag*`
constants — never a password, key, seed, mnemonic, signature, wallet path, or
per-user identifier. The KDF-duration histogram deliberately measures the
already-public Argon2/PBKDF2 work factor; do not add finer timers around the
password bytes themselves.

---

## Platform notes

- **Windows** is the only platform with a real pin store today (`DpapiPinStore`,
  guarded by `[SupportedOSPlatform("windows")]` and an
  `OperatingSystem.IsWindows()` check in `PinStores`). Everything else runs with
  `NullPinStore` — fully functional, just no rollback protection on the wallet
  file.
- The project sets `InvariantGlobalization` — mnemonic normalisation uses
  `FormKD` and does not depend on ICU locale data.
- The BIP39 English wordlist is an **embedded resource** (`wordlist_english.txt`,
  logical name unchanged), loaded via `Assembly.GetExecutingAssembly()`. If you
  merge/trim assemblies, keep the resource.
- **Reproducible-build flags:** `Deterministic` is on; `ContinuousIntegrationBuild`
  is set only when `$(CI)` is present. These are necessary-but-not-sufficient —
  see `doc/THREAT_MODEL.md`.

---

## Building and testing

```sh
dotnet build   Svrn7.Trust.KeyWallet/Svrn7.Trust.KeyWallet.csproj
dotnet test    KeyWallet.Tests/KeyWallet.Tests.csproj      # xUnit; covers this library
dotnet pack -c Release Svrn7.Trust.KeyWallet/Svrn7.Trust.KeyWallet.csproj
```

`KeyWallet.Tests` exercises the primitives and the `WalletService` orchestration
(throttle/pin/TOFU/diagnostics ordering, recovery-phrase determinism, session
key lifecycle).

---

## API surface at a glance

```
WalletService                 facade: Create, Unlock, Save, TryInspect,
                              DeriveFromRecoveryPhrase, CreateFromRecoveryPhrase,
                              GenerateRecoveryPhrase, WalletFileExists, PinStore
  UnlockResult                Success | NoWalletFile | Throttled | PinMismatch | WrongPassword
  WalletWriteResult           (KeyPair, PublicKeyBase64, PublicKeyPinned)
  WalletInspection            (PublicKeyBase64, PinCheck)

KeyPair : IDisposable         Generate, FromSeed, FromPrivateKey, Sign, Verify,
                              PublicKeyBase64, PublicKeySubjectPublicKeyInfo, PrivateKeyPkcs8
KeyPairSession : IDisposable  Current, IsUnlocked, Replace, Lock

WalletFile                    Create, Save, Load, Unlock, Version, PublicKeyBase64
WalletCrypto (static)         EncryptV1/DecryptV1, EncryptV2/DecryptV2, DeriveKey*, NewSalt
Mnemonic (static)             Generate, Validate, ToSeed
EcMath (static)               SelfTest, ScalarMultiplyBasePoint, Order, …

IPinStore                     Enabled, TryGet, Set, Remove
  DpapiPinStore (Windows)     .DefaultPath
  InMemoryPinStore
  NullPinStore
PinStores (static)            CreateDefault() -> PinStoreResult(Store, UnavailableReason)
WalletPin (static)            Compute(spki | base64) -> SHA-256
PinnedWallet (static)         Load(path, walletId, store) -> (Wallet, Check, ActualPin)
PinCheck                      Match | FirstUse | Mismatch

UnlockThrottle                Load, GetRemainingWait, RecordFailure, Reset
KeyWalletDiagnostics (static) ActivitySource, Meter, instruments,
                              RecordUnlockResult, RecordSignResult, RecordWalletWritten
KeyWalletResult (static)      success | wrong_password | throttled | pin_mismatch | locked | error | auth_failed
```

See [`doc/THREAT_MODEL.md`](doc/THREAT_MODEL.md) for what this does and does not
protect against.
