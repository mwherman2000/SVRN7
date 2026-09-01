using System.Diagnostics;
using System.Security.Cryptography;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// Outcome of <see cref="WalletService.Unlock"/>. Exactly one of the nested
/// subtypes is returned; switch on it. Only <see cref="Success"/> carries a
/// key -- the throttle and pin checks that produce the other cases run
/// before any password is read.
/// </summary>
public abstract record UnlockResult
{
    private UnlockResult() { }

    /// <summary>
    /// Password accepted. The caller owns <paramref name="KeyPair"/> and must
    /// dispose it. <paramref name="PinnedOnFirstUse"/> is true when this
    /// unlock also enrolled the wallet's public key as its pin (trust on
    /// first use).
    /// </summary>
    public sealed record Success(KeyPair KeyPair, bool PinnedOnFirstUse) : UnlockResult;

    /// <summary>No wallet file exists at the service's path yet.</summary>
    public sealed record NoWalletFile(string Path) : UnlockResult;

    /// <summary>
    /// Too many recent failed attempts; no password was checked. Retry once
    /// <paramref name="RetryAfter"/> has elapsed.
    /// </summary>
    public sealed record Throttled(TimeSpan RetryAfter) : UnlockResult;

    /// <summary>
    /// The wallet file's public key does not match the pinned key for this
    /// wallet -- the file was replaced or rolled back. No password was
    /// checked. Both values are SHA-256 of the SPKI public key.
    /// </summary>
    public sealed record PinMismatch(byte[] PinnedHash, byte[] ActualHash) : UnlockResult;

    /// <summary>
    /// Wrong password, or the encrypted private key failed its authentication
    /// tag (tampered ciphertext) -- the two are indistinguishable by design.
    /// A throttle failure has been recorded.
    /// </summary>
    public sealed record WrongPassword : UnlockResult;
}

/// <summary>
/// A wallet file was written to disk. The caller owns <paramref name="KeyPair"/>
/// and must dispose it; for a plain <see cref="WalletService.Save"/> call
/// (e.g. a password change) it is the same instance that was passed in.
/// </summary>
public sealed record WalletWriteResult(KeyPair KeyPair, string PublicKeyBase64, bool PublicKeyPinned);

/// <summary>Read-only view of the wallet file's public key and how it compares to the pin store.</summary>
public sealed record WalletInspection(string PublicKeyBase64, PinCheck PinCheck);

/// <summary>
/// Composes the wallet primitives -- <see cref="WalletFile"/>,
/// <see cref="WalletCrypto"/>, <see cref="Mnemonic"/>, <see cref="IPinStore"/>,
/// <see cref="UnlockThrottle"/> -- into the full wallet operations, applying
/// unlock throttling, public-key pinning with trust-on-first-use enrollment,
/// and <see cref="KeyWalletDiagnostics"/> instrumentation in the order those
/// steps have to happen (e.g. a pin is never enrolled before a password is
/// verified; a wrong password always records a throttle failure).
///
/// UI-free: every method returns data or a result object and never writes to
/// a console. Password material is passed as <c>char[]</c> the caller still
/// owns and should zero after the call; <see cref="Unlock"/> zeroes the
/// array its provider returns. See the KeyWallet console app for a reference
/// host.
///
/// One instance is bound to one wallet path. Cheap to construct; not
/// thread-safe.
/// </summary>
public sealed class WalletService
{
    private readonly string _walletPath;
    private readonly string _walletId;
    private readonly IPinStore _pinStore;

    /// <param name="walletPath">Path to the wallet JSON file. It need not exist yet.</param>
    /// <param name="pinStore">
    /// Backing store for public-key pinning. Pass <see cref="NullPinStore"/>
    /// to run without pinning. The caller owns the store's lifetime; see
    /// <see cref="PinStores.CreateDefault"/> for the usual choice.
    /// </param>
    /// <param name="walletId">
    /// Key under which this wallet's pin is stored. Defaults to the absolute
    /// wallet path, so moving or renaming the file reads as a new wallet
    /// (first use). Pass an explicit stable id if the file can legitimately
    /// move.
    /// </param>
    public WalletService(string walletPath, IPinStore pinStore, string? walletId = null)
    {
        _walletPath = walletPath;
        _pinStore = pinStore;
        _walletId = walletId ?? Path.GetFullPath(walletPath);
    }

    /// <summary>True if a wallet file currently exists at this service's path.</summary>
    public bool WalletFileExists => File.Exists(_walletPath);

    /// <summary>The pin store this service was constructed with.</summary>
    public IPinStore PinStore => _pinStore;

    /// <summary>
    /// Attempts to unlock the wallet. The throttle check and the public-key
    /// pin check run first; <paramref name="passwordProvider"/> is invoked
    /// only when both pass, so a throttled or tampered wallet never prompts
    /// for a password. The <c>char[]</c> the provider returns is zeroed
    /// before this method returns.
    /// </summary>
    public UnlockResult Unlock(Func<char[]> passwordProvider)
    {
        using var activity = KeyWalletDiagnostics.ActivitySource.StartActivity("KeyWallet.Unlock");

        if (!File.Exists(_walletPath))
            return new UnlockResult.NoWalletFile(_walletPath);

        var throttle = UnlockThrottle.Load(_walletPath);
        var wait = throttle.GetRemainingWait();
        if (wait > TimeSpan.Zero)
        {
            KeyWalletDiagnostics.RecordUnlockResult(activity, KeyWalletResult.Throttled);
            return new UnlockResult.Throttled(wait);
        }

        var (walletFile, pinCheck, actualPin) = PinnedWallet.Load(_walletPath, _walletId, _pinStore);
        if (pinCheck == PinCheck.Mismatch)
        {
            KeyWalletDiagnostics.RecordUnlockResult(activity, KeyWalletResult.PinMismatch);
            return new UnlockResult.PinMismatch(_pinStore.TryGet(_walletId)!, actualPin);
        }

        var password = passwordProvider();
        try
        {
            KeyPair keyPair;
            try
            {
                keyPair = walletFile.Unlock(password); // throws CryptographicException on a bad password
            }
            catch (CryptographicException)
            {
                throttle.RecordFailure(_walletPath);
                KeyWalletDiagnostics.RecordUnlockResult(activity, KeyWalletResult.WrongPassword);
                return new UnlockResult.WrongPassword();
            }

            UnlockThrottle.Reset(_walletPath);

            var pinnedOnFirstUse = pinCheck == PinCheck.FirstUse && _pinStore.Enabled;
            if (pinnedOnFirstUse)
            {
                // Trust-on-first-use: a correct password proves the user owns
                // this wallet, so adopt its key as the pin. Covers wallets
                // created before pinning existed.
                _pinStore.Set(_walletId, actualPin);
            }

            KeyWalletDiagnostics.RecordUnlockResult(activity, KeyWalletResult.Success);
            return new UnlockResult.Success(keyPair, pinnedOnFirstUse);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>
    /// Generates a fresh random key pair, encrypts it under
    /// <paramref name="password"/>, and writes the wallet file. Resets unlock
    /// throttling and pins the new public key. The returned key is unlocked
    /// in memory; dispose it (immediately, if the caller only wanted the
    /// wallet on disk).
    /// </summary>
    public WalletWriteResult Create(char[] password)
    {
        var keyPair = KeyPair.Generate();
        try
        {
            return Save(keyPair, password, fromRecoveryPhrase: false);
        }
        catch
        {
            keyPair.Dispose();
            throw;
        }
    }

    /// <summary>A new BIP39-style 12-word recovery phrase. Not persisted anywhere -- show it once, then forget it.</summary>
    public static string GenerateRecoveryPhrase() => Mnemonic.Generate();

    /// <summary>
    /// Validates <paramref name="mnemonic"/> and deterministically derives its
    /// key pair, without touching disk or the pin store. Use this when the
    /// phrase must be shown or confirmed before the wallet is (or isn't)
    /// persisted; call <see cref="Save"/> afterwards to write it.
    /// </summary>
    /// <exception cref="FormatException">The phrase has the wrong word count, an unknown word, or a bad checksum.</exception>
    public KeyPair DeriveFromRecoveryPhrase(string mnemonic)
    {
        Mnemonic.Validate(mnemonic);
        var seed = Mnemonic.ToSeed(mnemonic);
        try
        {
            return KeyPair.FromSeed(seed);
        }
        finally
        {
            Array.Clear(seed);
        }
    }

    /// <summary>
    /// <see cref="DeriveFromRecoveryPhrase"/> followed by <see cref="Save"/>
    /// in one step, tagged as a recovery-phrase write. The returned key is
    /// unlocked in memory; the caller owns it.
    /// </summary>
    /// <exception cref="FormatException">The phrase is not valid.</exception>
    public WalletWriteResult CreateFromRecoveryPhrase(string mnemonic, char[] password)
    {
        var keyPair = DeriveFromRecoveryPhrase(mnemonic);
        try
        {
            return Save(keyPair, password, fromRecoveryPhrase: true);
        }
        catch
        {
            keyPair.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encrypts <paramref name="keyPair"/> under <paramref name="password"/>
    /// and writes the wallet file (atomic replace, previous contents kept at
    /// "<c>.bak</c>"). Resets unlock throttling and re-pins the public key.
    /// Does <b>not</b> take ownership of <paramref name="keyPair"/> -- the
    /// caller still disposes it. Used directly to change a password or to
    /// persist a key obtained from <see cref="DeriveFromRecoveryPhrase"/>.
    /// </summary>
    /// <param name="fromRecoveryPhrase">
    /// Whether this write originates from a recovery-phrase flow. Feeds the
    /// <c>keywallet.with_recovery_phrase</c> diagnostics tag only; has no
    /// effect on what is written.
    /// </param>
    public WalletWriteResult Save(KeyPair keyPair, char[] password, bool fromRecoveryPhrase = false)
    {
        var walletFile = WalletFile.Create(keyPair, password);
        walletFile.Save(_walletPath);
        UnlockThrottle.Reset(_walletPath); // fresh content -- any prior lockout no longer applies

        var pinned = _pinStore.Enabled;
        if (pinned)
            _pinStore.Set(_walletId, WalletPin.Compute(keyPair.PublicKeyBase64));

        KeyWalletDiagnostics.RecordWalletWritten(fromRecoveryPhrase);
        return new WalletWriteResult(keyPair, keyPair.PublicKeyBase64, pinned);
    }

    /// <summary>
    /// The wallet file's public key and its pin status, or null if no wallet
    /// file exists. Read-only: unlike <see cref="Unlock"/> this never refuses
    /// on a pin mismatch -- the caller decides what a
    /// <see cref="PinCheck.Mismatch"/> means for a read.
    /// </summary>
    public WalletInspection? TryInspect()
    {
        if (!File.Exists(_walletPath))
            return null;

        var (walletFile, pinCheck, _) = PinnedWallet.Load(_walletPath, _walletId, _pinStore);
        return new WalletInspection(walletFile.PublicKeyBase64, pinCheck);
    }
}
