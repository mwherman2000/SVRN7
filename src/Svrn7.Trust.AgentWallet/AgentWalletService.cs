using System.Diagnostics;
using System.Security.Cryptography;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// Outcome of <see cref="AgentWalletService.Unlock"/>. Exactly one nested
/// subtype is returned; switch on it. Only <see cref="Success"/> carries key
/// material — the throttle and pin checks that produce the other cases run
/// before any password is read.
/// </summary>
public abstract record AgentUnlockResult
{
    private AgentUnlockResult() { }

    /// <summary>Password accepted. The caller owns <see cref="Identity"/> and must dispose it.</summary>
    public sealed record Success(AgentIdentity Identity) : AgentUnlockResult;

    /// <summary>No wallet file exists at the service's path yet.</summary>
    public sealed record NoWallet(string Path) : AgentUnlockResult;

    /// <summary>Too many recent failed attempts; no password was checked. Retry after <see cref="RetryAfter"/>.</summary>
    public sealed record Throttled(TimeSpan RetryAfter) : AgentUnlockResult;

    /// <summary>
    /// The wallet file's public key does not match the pinned key for this
    /// wallet — the file was replaced or rolled back. No password was checked.
    /// Both values are SHA-256 of the compressed secp256k1 public key.
    /// </summary>
    public sealed record PinMismatch(byte[] PinnedHash, byte[] ActualHash) : AgentUnlockResult;

    /// <summary>Wrong password, or the sealed payload failed its authentication tag. A throttle failure has been recorded.</summary>
    public sealed record WrongPassword : AgentUnlockResult;
}

/// <summary>Read-only view of the wallet file's cleartext header — no decryption.</summary>
public sealed record AgentWalletInspection(string Secp256k1PublicKeyHex, string GenesisHashHex, PinCheck PinCheck);

/// <summary>New and old database master keys from <see cref="AgentWalletService.RotateDatabaseKey"/>. Both must be zeroed by the caller after the rebuild.</summary>
public sealed record DatabaseKeyRotation(byte[] OldKey, byte[] NewKey);

/// <summary>
/// Composes the AgentWallet primitives — <see cref="AgentWalletFile"/>,
/// <see cref="WalletCrypto"/>, <see cref="RecoveryPhrase"/>,
/// <see cref="IPinStore"/>, <see cref="UnlockThrottle"/> — into the full wallet
/// operations, applying unlock throttling, public-key pinning with
/// trust-on-first-use, and <see cref="AgentWalletDiagnostics"/> instrumentation
/// in the order those steps must happen (docs/AGENTWALLET.md §9).
///
/// UI-free: every method returns data or a result object and never writes to a
/// console. Password material is passed as <c>char[]</c> the caller still owns
/// and should zero after the call; <see cref="Unlock"/> zeroes the array its
/// provider returns.
///
/// One instance is bound to one wallet path. Cheap to construct; not
/// thread-safe.
/// </summary>
public sealed class AgentWalletService
{
    private readonly string _walletPath;
    private readonly string _walletId;
    private readonly IPinStore _pinStore;

    /// <param name="walletPath">Path to <c>agent-identity.wallet</c>. It need not exist yet.</param>
    /// <param name="pinStore">Backing store for public-key pinning. Pass <see cref="NullPinStore"/> to run without pinning.</param>
    /// <param name="walletId">Key under which this wallet's pin is stored. Defaults to the absolute wallet path.</param>
    public AgentWalletService(string walletPath, IPinStore pinStore, string? walletId = null)
    {
        _walletPath = walletPath;
        _pinStore = pinStore;
        _walletId = walletId ?? Path.GetFullPath(walletPath);
    }

    public bool WalletExists => File.Exists(_walletPath);

    public IPinStore PinStore => _pinStore;

    // ── Create ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new wallet: derives (or restores, if <paramref name="recoveryPhrase"/>
    /// is given) the secp256k1 + X25519 keys, generates a random 32-byte DB
    /// master key, encrypts the payload under <paramref name="password"/>, and
    /// writes the file atomically. Resets unlock throttling and pins the new
    /// public key. Returns the identity <b>unlocked</b>; the caller owns and
    /// disposes it.
    /// </summary>
    /// <param name="didFromGenesisHash">
    /// Given the 64-hex genesis hash, returns the DID string. The SVRN7 rule is
    /// <c>hash =&gt; $"did:drn:wanderer.svrn7.net/agent/1.0/{hash}"</c>; kept as a
    /// callback so this library carries no DID-format knowledge.
    /// </param>
    /// <exception cref="InvalidOperationException">A wallet file already exists at this path.</exception>
    /// <exception cref="FormatException"><paramref name="recoveryPhrase"/> is not a valid 12-word BIP39 phrase.</exception>
    public AgentIdentity Create(
        char[] password,
        Func<string, string> didFromGenesisHash,
        string role,
        string? recoveryPhrase = null,
        string? parentTdaDid = null)
    {
        var operation = recoveryPhrase is null ? "create" : "restore";
        using var activity = AgentWalletDiagnostics.ActivitySource.StartActivity("AgentWallet.Create");
        activity?.SetTag(AgentWalletDiagnostics.TagOperation, operation);
        activity?.SetTag("agentwallet.has_parent", !string.IsNullOrEmpty(parentTdaDid));

        if (WalletExists)
            throw new InvalidOperationException($"A wallet already exists at '{_walletPath}'.");

        var phrase = recoveryPhrase ?? RecoveryPhrase.Generate();
        using var keys = RecoveryPhrase.Derive(phrase);

        var genesisHash = GenesisHash.Compute(keys.Secp256k1PublicKeyHex);
        var did = didFromGenesisHash(genesisHash);
        var dbMaster = RandomNumberGenerator.GetBytes(32);
        var createdUtc = DateTimeOffset.UtcNow.ToString("O");

        var payload = new AgentWalletPayload
        {
            Did = did,
            Role = role,
            CreatedUtc = createdUtc,
            Secp256k1PrivateKeyHex = Convert.ToHexString(keys.Secp256k1PrivateKey).ToLowerInvariant(),
            Secp256k1PublicKeyHex = keys.Secp256k1PublicKeyHex,
            X25519PrivateKeyHex = Convert.ToHexString(keys.X25519PrivateKey).ToLowerInvariant(),
            X25519PublicKeyHex = keys.X25519PublicKeyHex,
            ParentTdaDid = string.IsNullOrEmpty(parentTdaDid) ? null : parentTdaDid,
            RecoveryPhrase = phrase,
            Bip39EntropyBits = 128,
            DbMasterKeyHex = Convert.ToHexString(dbMaster).ToLowerInvariant()
        };

        WriteWallet(payload, password, operation);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return BuildIdentity(payload, dbMaster, genesisHash);
    }

    // ── Unlock ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to unlock the wallet. The throttle check and the public-key pin
    /// check run first; <paramref name="passwordProvider"/> is invoked only when
    /// both pass. The <c>char[]</c> the provider returns is zeroed before this
    /// method returns.
    /// </summary>
    public AgentUnlockResult Unlock(Func<char[]> passwordProvider)
    {
        using var activity = AgentWalletDiagnostics.ActivitySource.StartActivity("AgentWallet.Unlock");

        if (!WalletExists)
        {
            AgentWalletDiagnostics.RecordUnlockResult(activity, AgentWalletResult.NoWallet);
            return new AgentUnlockResult.NoWallet(_walletPath);
        }

        var throttle = UnlockThrottle.Load(_walletPath);
        var wait = throttle.GetRemainingWait();
        if (wait > TimeSpan.Zero)
        {
            AgentWalletDiagnostics.RecordUnlockResult(activity, AgentWalletResult.Throttled);
            return new AgentUnlockResult.Throttled(wait);
        }

        var file = AgentWalletFile.Load(_walletPath);
        var actualPin = WalletPin.Compute(file.Secp256k1PublicKeyHex);
        var pinned = _pinStore.TryGet(_walletId);
        var pinCheck = pinned is null
            ? PinCheck.FirstUse
            : CryptographicOperations.FixedTimeEquals(pinned, actualPin) ? PinCheck.Match : PinCheck.Mismatch;
        AgentWalletDiagnostics.RecordPinCheck(pinCheck);

        if (pinCheck == PinCheck.Mismatch)
        {
            AgentWalletDiagnostics.RecordUnlockResult(activity, AgentWalletResult.PinMismatch);
            return new AgentUnlockResult.PinMismatch(pinned!, actualPin);
        }

        var password = passwordProvider();
        var startTs = Stopwatch.GetTimestamp();
        try
        {
            AgentWalletPayload payload;
            try
            {
                payload = file.Decrypt(password);
            }
            catch (CryptographicException)
            {
                AgentWalletDiagnostics.RecordKdfDuration(startTs, AgentWalletResult.WrongPassword);
                throttle.RecordFailure(_walletPath);
                AgentWalletDiagnostics.RecordUnlockResult(activity, AgentWalletResult.WrongPassword);
                return new AgentUnlockResult.WrongPassword();
            }

            AgentWalletDiagnostics.RecordKdfDuration(startTs, AgentWalletResult.Success);
            UnlockThrottle.Reset(_walletPath);

            if (pinCheck == PinCheck.FirstUse && _pinStore.Enabled)
                _pinStore.Set(_walletId, actualPin);

            var dbMaster = Convert.FromHexString(payload.DbMasterKeyHex);
            var identity = BuildIdentity(payload, dbMaster, GenesisHash.Compute(payload.Secp256k1PublicKeyHex));

            AgentWalletDiagnostics.RecordUnlockResult(activity, AgentWalletResult.Success);
            return new AgentUnlockResult.Success(identity);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    // ── Maintenance ────────────────────────────────────────────────────────

    /// <summary>
    /// Re-encrypts the payload under <paramref name="newPassword"/> and rewrites
    /// the wallet. The DB master key value is unchanged, so <b>no database is
    /// re-keyed</b>. Resets unlock throttling and re-pins.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The current password is wrong or the wallet is throttled/tampered.</exception>
    public void ChangePassword(Func<char[]> currentPasswordProvider, char[] newPassword)
    {
        var current = currentPasswordProvider();
        try
        {
            var payload = DecryptOrThrow(current);
            WriteWallet(payload, newPassword, "change_password");
        }
        finally
        {
            Array.Clear(current);
        }
    }

    /// <summary>
    /// Generates a fresh 32-byte DB master key, stores it in the payload, and
    /// rewrites the wallet under the same password. Returns both keys so the
    /// caller can rebuild each database (open with <see cref="DatabaseKeyRotation.OldKey"/>,
    /// rebuild with <see cref="DatabaseKeyRotation.NewKey"/>). Caller zeroes both.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The password is wrong or the wallet is throttled/tampered.</exception>
    public DatabaseKeyRotation RotateDatabaseKey(Func<char[]> passwordProvider)
    {
        var password = passwordProvider();
        try
        {
            var payload = DecryptOrThrow(password);
            var oldKey = Convert.FromHexString(payload.DbMasterKeyHex);
            var newKey = RandomNumberGenerator.GetBytes(32);
            payload.DbMasterKeyHex = Convert.ToHexString(newKey).ToLowerInvariant();
            WriteWallet(payload, password, "rotate_db_key");
            var ret = new DatabaseKeyRotation(oldKey, (byte[])newKey.Clone());
            CryptographicOperations.ZeroMemory(newKey);
            return ret;
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>The 12-word recovery phrase from the wallet payload, or null if the wallet holds none.</summary>
    /// <exception cref="UnauthorizedAccessException">The password is wrong or the wallet is throttled/tampered.</exception>
    public string? ExportRecoveryPhrase(Func<char[]> passwordProvider)
    {
        var password = passwordProvider();
        try
        {
            var payload = DecryptOrThrow(password);
            return string.IsNullOrWhiteSpace(payload.RecoveryPhrase) ? null : payload.RecoveryPhrase;
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>The wallet file's cleartext header and its pin status, or null if no wallet file exists. Never decrypts.</summary>
    public AgentWalletInspection? Inspect()
    {
        if (!WalletExists) return null;

        var file = AgentWalletFile.Load(_walletPath);
        var actualPin = WalletPin.Compute(file.Secp256k1PublicKeyHex);
        var pinned = _pinStore.TryGet(_walletId);
        var check = pinned is null
            ? PinCheck.FirstUse
            : CryptographicOperations.FixedTimeEquals(pinned, actualPin) ? PinCheck.Match : PinCheck.Mismatch;

        return new AgentWalletInspection(
            file.Secp256k1PublicKeyHex,
            GenesisHash.Compute(file.Secp256k1PublicKeyHex),
            check);
    }

    // ── internals ──────────────────────────────────────────────────────────

    private void WriteWallet(AgentWalletPayload payload, char[] password, string operation)
    {
        var file = AgentWalletFile.Encrypt(payload, password);
        file.Save(_walletPath);
        UnlockThrottle.Reset(_walletPath);

        if (_pinStore.Enabled)
            _pinStore.Set(_walletId, WalletPin.Compute(payload.Secp256k1PublicKeyHex));

        AgentWalletDiagnostics.RecordWalletWritten(operation);
    }

    /// <summary>
    /// Runs the throttle + pin + decrypt path and returns the payload, or throws
    /// with the reason. Used by the maintenance methods, which have no
    /// result-type surface. Does <b>not</b> clear <paramref name="password"/> —
    /// the caller owns it.
    /// </summary>
    private AgentWalletPayload DecryptOrThrow(char[] password)
    {
        if (!WalletExists)
            throw new FileNotFoundException($"No wallet at '{_walletPath}'.", _walletPath);

        var throttle = UnlockThrottle.Load(_walletPath);
        var wait = throttle.GetRemainingWait();
        if (wait > TimeSpan.Zero)
            throw new UnauthorizedAccessException($"Wallet is locked out for another {wait.TotalSeconds:0}s after repeated failures.");

        var file = AgentWalletFile.Load(_walletPath);

        var pinned = _pinStore.TryGet(_walletId);
        if (pinned is not null &&
            !CryptographicOperations.FixedTimeEquals(pinned, WalletPin.Compute(file.Secp256k1PublicKeyHex)))
            throw new UnauthorizedAccessException("Wallet public key does not match its pin — the file was replaced or rolled back.");

        try
        {
            return file.Decrypt(password);
        }
        catch (CryptographicException ex)
        {
            throttle.RecordFailure(_walletPath);
            throw new UnauthorizedAccessException("Wrong wallet password.", ex);
        }
    }

    private static AgentIdentity BuildIdentity(AgentWalletPayload payload, byte[] dbMaster, string genesisHash) =>
        new(
            Convert.FromHexString(payload.Secp256k1PrivateKeyHex),
            payload.Secp256k1PublicKeyHex,
            Convert.FromHexString(payload.X25519PrivateKeyHex),
            payload.X25519PublicKeyHex,
            dbMaster,
            payload.Did,
            payload.Role,
            payload.ParentTdaDid,
            genesisHash);
}
