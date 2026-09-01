# KeyWallet Threat Model

A locally-stored, password-protected EC key pair. This document is a plain
statement of what KeyWallet protects against, what it explicitly does not,
and how to interpret the on-disk format -- so trust in it is based on what's
actually true, not assumed.

## What's protected

- **The private key at rest** is always encrypted (AES-256-GCM) under a
  password-derived key. The plaintext private key never touches disk.
- **Tamper evidence**: AES-GCM's authentication tag means a corrupted or
  modified encrypted blob fails to decrypt (`CryptographicException`/
  `AuthenticationTagMismatchException`, a subclass of it) rather than
  silently returning garbage key material.
- **Wallet-file substitution / rollback (Windows)**: `wallet.json`'s own
  `PublicKeyBase64` is self-asserted -- an attacker who swaps in their own
  wallet supplies a matching public key in the same file. Public-key
  *pinning* is the independent check. On wallet creation (and on the first
  successful unlock of a wallet that predates pinning), KeyWallet records a
  SHA-256 of the wallet's public key in a DPAPI-protected pin store at
  `%LOCALAPPDATA%\KeyWallet\pin-store.bin`, sealed to the current Windows
  user (`DpapiPinStore` / `IPinStore`). Every later unlock re-derives that
  hash from `wallet.json` and *refuses before prompting for a password* if
  it doesn't match -- so dropping in a different `wallet.json`, or rolling
  one back to an older key (e.g. restoring `wallet.json.bak` from before a
  key rotation), is caught. The pin store holds only public-key hashes, not
  secrets, so a corrupted or foreign pin store fails open with a warning
  (pinning disabled for that session) rather than locking you out. See the
  limits below.
- **Wrong-password detection**: same mechanism -- a wrong password fails the
  GCM tag check and throws, it does not silently unlock a different key.
- **Write durability**: `WalletFile.Save` writes to a temp file, flushes it
  to disk, then atomically swaps it into place (keeping the previous
  contents at `wallet.json.bak`). A crash or power loss mid-write can't
  leave `wallet.json` missing or half-written.
- **In-memory key lifetime**: `KeyPair` is `IDisposable` and zeroes its
  private key bytes on Lock/re-unlock/exit; passwords are handled as
  `char[]` (not `string`) end-to-end from console entry through the KDF, and
  explicitly cleared after use, since strings can't be reliably zeroed.
- **Repeated wrong-password guessing through this app's own prompt** is
  throttled (`UnlockThrottle`): the first 2 failed unlock attempts are free,
  then each further attempt adds an exponentially growing wait (capped at 5
  minutes) before the app will even prompt for a password again. The count
  persists in a `wallet.json.lockout` sidecar, so restarting the process
  doesn't reset it. This is deliberately a bounded backoff, not a hard
  lockout after N attempts -- a permanent lockout on a single-user local
  wallet would just turn a typo streak into a self-inflicted denial of
  service. See the note below on what this does and does not defend
  against.

## What's explicitly NOT protected

- **Offline brute-forcing of a copied wallet file.** `UnlockThrottle` only
  slows down guesses made through KeyWallet's own interactive prompt.
  Anyone who copies `wallet.json` to another machine and writes their own
  brute-forcer bypasses it entirely -- the actual defense against that is
  the KDF cost (Argon2id/PBKDF2), not the throttle. The throttle is
  defense-in-depth for the "someone guessing a few passwords at your
  keyboard, or scripting attempts against this binary" scenario, not the
  primary control.
- **Wallet-file substitution outside the pinning envelope.** Public-key
  pinning (above) catches a plain file swap, but note what it does *not*
  cover: (a) it is Windows-only right now -- on other platforms the pin
  store is a no-op and every load is treated as first use; (b) code
  running as the same Windows user can re-seal its own pin-store entry
  (and can keylog the password anyway), so pinning adds nothing against a
  compromised account; (c) enrollment is trust-on-first-use -- if the
  wallet is already the attacker's when it is first pinned, pinning
  faithfully protects the wrong key. It detects change *after* enrollment,
  not a bad starting state.
- **Deletion of `wallet.json` (and `wallet.json.bak`).** Pinning detects
  substitution, not removal. Nothing here stops an actor who can write to
  the wallet directory from deleting the files outright -- that is a
  denial of service, not a key compromise, and the recovery path for it is
  your own backups / recovery phrase.
- **A compromised or malicious host.** Malware, a keylogger, or another
  process with debugger-level access while the wallet is unlocked can read
  the password as it's typed or the key while it's live in process memory.
  Nothing here defends against a compromised machine.
- **Live-process memory inspection.** Key zeroing here is best-effort
  (`CryptographicOperations.ZeroMemory`), not backed by `mlock`/swap
  protection. The key can still be paged to disk by the OS, or captured by
  a memory dump, while the wallet is unlocked.
- **An unsigned binary.** The KeyWallet executable itself is not code-signed
  in this build. Nothing verifies the binary you're running hasn't been
  tampered with.
- **Loss of both the password and the recovery phrase.** There is no other
  recovery path. If you use password-only wallet creation (option 1, no
  recovery phrase), losing the password means losing the key permanently.
- **Portability to other BIP32/44 wallets.** The recovery-phrase feature
  follows standard BIP39 mnemonic<->seed steps, but how the 64-byte seed
  becomes a P-256 private key (`KeyPair.FromSeed`/`EcMath`) is this wallet's
  own construction -- BIP32 HD derivation is defined for secp256k1, not
  P-256. A phrase generated here recovers the key in *this* app only; it
  will not import into a standard BIP32/44 wallet.
- **A certified constant-time EC implementation.** `EcMath.ScalarMultiply`
  uses a Montgomery ladder (fixed iteration count, branch-free register
  selection) specifically to avoid leaking the private scalar's Hamming
  weight/bit length through timing, since it runs directly on the private
  key in `KeyPair.FromSeed`. This is a real mitigation of the dominant leak
  in the previous double-and-add implementation, but `System.Numerics.
  BigInteger`'s own arithmetic is not a certified constant-time primitive,
  so residual timing variance from the underlying bignum implementation is
  still possible. Treat this as best-effort, not a hardened guarantee.

## On-disk format: `Version` field

`wallet.json`'s `Version` field selects the KDF used for the encrypted
blob, so old wallet files keep working without any migration step:

- **`Version: 1`** (legacy): PBKDF2-SHA256, 600,000 iterations. A
  legitimate, still-recommended KDF, but weaker against GPU/ASIC brute
  force than Argon2id.
- **`Version: 2`** (current): Argon2id, 64 MiB memory / 3 iterations /
  4-way parallelism, with those parameters embedded in the blob itself.

New wallets (option 1, 2) always write Version 2. Existing Version 1
wallets are read unchanged and only upgrade to Version 2 the next time
their password is changed (option 8), since that re-encrypts through the
same `WalletFile.Create` path used for new wallets.

## Diagnostics: what can leave the process

KeyWallet defines one `ActivitySource` and one `Meter`, both named
`"KeyWallet"` (`KeyWalletDiagnostics`). It attaches **no exporter** -- with
nothing listening (the standalone default) every span and measurement is a
no-op. A host that embeds KeyWallet can point its own telemetry pipeline at
those two names.

What the instruments may emit is deliberately bounded:

- **Tags** are fixed-cardinality enum-like strings (`success`,
  `wrong_password`, `throttled`, `pin_mismatch`, `match`/`first_use`/
  `mismatch`, `argon2id`/`pbkdf2`) plus a `with_recovery_phrase` boolean.
  No password, private key, seed, mnemonic, **public key**, signature,
  wallet path, or per-user identifier is ever set as a tag.
- **`keywallet.unlock.kdf.duration`** times the KDF + AES-GCM open. It
  reflects the configured Argon2id/PBKDF2 work factor, which is already
  public (its parameters are stored in the wallet blob). No timer is placed
  around the password bytes themselves -- finer-grained timing there is how
  a metric becomes a side channel, and is out of scope by construction.

If you wire these to an external backend, that backend now sees your wallet
usage pattern (when and how often you unlock/sign) and KDF timings. That is
a deployment choice for the embedding app, not something KeyWallet does on
its own.

## Build reproducibility

Both `Svrn7.Trust.KeyWallet.csproj` (the reusable library) and
`KeyWallet.csproj` (the console reference host) set
`ContinuousIntegrationBuild` (standard MSBuild convention for reproducible
builds under a build server) and rely on Roslyn's deterministic
compilation (on by default). These are
necessary-but-not-sufficient steps toward a reproducible build; a real
guarantee requires an actual CI pipeline building from a pinned commit,
which is not set up for this project.
