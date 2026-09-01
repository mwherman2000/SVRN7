namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// Holds the app's own trusted copy of a wallet's public key -- the "pin".
///
/// WHY THIS EXISTS: <see cref="WalletFile"/> already carries
/// <see cref="WalletFile.PublicKeyBase64"/>, but that value is
/// self-asserted -- an attacker who replaces <c>wallet.json</c> with their
/// own wallet supplies a matching public key in the same file, so the file
/// alone can't tell you it's the wallet you enrolled. The pin is an
/// independent second copy, kept somewhere a plain file swap can't reach
/// (on Windows: DPAPI, sealed to the current OS user -- see
/// <see cref="DpapiPinStore"/>). On load, the wallet's public key is hashed
/// and compared against the pin; a mismatch means the file was replaced or
/// rolled back.
///
/// SCOPE: this defends against dropping a foreign <c>wallet.json</c> into
/// place without code execution as the enrolling user. It does not defend
/// against a process already running as that user (which can re-seal its
/// own pin, and can also keylog the password) -- that is out of scope per
/// THREAT_MODEL.md and pinning does not change it.
/// </summary>
public interface IPinStore
{
    /// <summary>
    /// False for a no-op store (unsupported OS, or the backing store could
    /// not be opened). Callers skip pin enrollment and pin messaging when
    /// this is false; verification simply always reports "first use".
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// The pinned public-key hash for <paramref name="walletId"/>, or null
    /// if nothing is pinned yet (first use).
    /// </summary>
    byte[]? TryGet(string walletId);

    /// <summary>Records or replaces the pin for <paramref name="walletId"/>.</summary>
    void Set(string walletId, byte[] publicKeyPin);

    /// <summary>Removes the pin for <paramref name="walletId"/> if one is present.</summary>
    void Remove(string walletId);
}

/// <summary>
/// A store that pins nothing. Used on non-Windows (no portable OS keystore
/// wired up yet) and as the fallback when the real store can't be opened.
/// Every verification against it reports "first use", so it never produces
/// a false mismatch.
/// </summary>
public sealed class NullPinStore : IPinStore
{
    public bool Enabled => false;
    public byte[]? TryGet(string walletId) => null;
    public void Set(string walletId, byte[] publicKeyPin) { }
    public void Remove(string walletId) { }
}

/// <summary>
/// Non-persistent <see cref="IPinStore"/>. Useful for tests and for
/// embedding scenarios that manage their own persistence. Pins live only
/// for the lifetime of the instance.
/// </summary>
public sealed class InMemoryPinStore : IPinStore
{
    private readonly Dictionary<string, byte[]> _pins = new();

    public bool Enabled => true;

    public byte[]? TryGet(string walletId) =>
        _pins.TryGetValue(walletId, out var pin) ? (byte[])pin.Clone() : null;

    public void Set(string walletId, byte[] publicKeyPin) =>
        _pins[walletId] = (byte[])publicKeyPin.Clone();

    public void Remove(string walletId) => _pins.Remove(walletId);
}

/// <summary>
/// The store <see cref="PinStores.CreateDefault"/> selected, plus the reason
/// a real store could not be opened (null when one was, or when the platform
/// has no store to open).
/// </summary>
public sealed record PinStoreResult(IPinStore Store, string? UnavailableReason);

/// <summary>
/// Picks the right <see cref="IPinStore"/> for the current machine so hosts
/// don't each re-implement the platform check and the fail-open fallback.
/// </summary>
public static class PinStores
{
    /// <summary>
    /// Windows: a <see cref="DpapiPinStore"/> at <see cref="DpapiPinStore.DefaultPath"/>.
    /// Any other OS: a <see cref="NullPinStore"/> (no portable OS keystore is
    /// wired up yet). If the Windows store exists but can't be opened -- most
    /// often a pin file written by a different Windows user -- the returned
    /// <see cref="PinStoreResult.Store"/> is a <see cref="NullPinStore"/> and
    /// <see cref="PinStoreResult.UnavailableReason"/> carries the message.
    /// Pinning deliberately fails open: the pin file holds only public-key
    /// hashes, so a broken one must never lock a user out of their wallet.
    /// </summary>
    public static PinStoreResult CreateDefault()
    {
        if (!OperatingSystem.IsWindows())
            return new PinStoreResult(new NullPinStore(), null);

        try
        {
            return new PinStoreResult(new DpapiPinStore(DpapiPinStore.DefaultPath), null);
        }
        catch (Exception ex)
        {
            return new PinStoreResult(new NullPinStore(), ex.Message);
        }
    }
}
