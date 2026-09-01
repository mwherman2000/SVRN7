namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// Owns the single in-memory unlocked <see cref="KeyPair"/> for a wallet
/// session and its lifetime. <see cref="Replace"/> zeroes the previously
/// held key before taking a new one -- unless the very same instance is
/// handed back (e.g. a re-encrypt that returns the still-live key), which
/// must not be disposed out from under the session.
///
/// This is a convenience for interactive hosts that keep one wallet
/// unlocked at a time; the library never requires it. Not thread-safe --
/// guard it yourself if more than one thread can unlock or lock.
/// </summary>
public sealed class KeyPairSession : IDisposable
{
    /// <summary>The currently unlocked key, or null when locked.</summary>
    public KeyPair? Current { get; private set; }

    /// <summary>True while a key is unlocked in memory.</summary>
    public bool IsUnlocked => Current is not null;

    /// <summary>
    /// Swaps in <paramref name="next"/> (pass null to lock), disposing the
    /// previously held key first unless it is the same instance being handed
    /// back.
    /// </summary>
    public void Replace(KeyPair? next)
    {
        if (!ReferenceEquals(Current, next))
            Current?.Dispose();
        Current = next;
    }

    /// <summary>Locks the session: disposes and clears the current key.</summary>
    public void Lock() => Replace(null);

    /// <summary>Disposes the held key, if any.</summary>
    public void Dispose() => Current?.Dispose();
}
