using System.Security.Cryptography;

namespace Svrn7.Trust.KeyWallet;

/// <summary>Result of comparing a loaded wallet's public key against the pin store.</summary>
public enum PinCheck
{
    /// <summary>The wallet's public key matches the pinned value.</summary>
    Match,

    /// <summary>Nothing is pinned for this wallet yet (trust-on-first-use), or pinning is disabled.</summary>
    FirstUse,

    /// <summary>A pin exists and the wallet's public key does NOT match it -- the file was replaced or rolled back.</summary>
    Mismatch
}

/// <summary>
/// Computes the pin value for a public key. The pin is SHA-256 of the raw
/// SubjectPublicKeyInfo DER bytes -- a public key is not secret, so what's
/// wanted here is a short, fixed-size fingerprint to compare, not
/// confidentiality. Base64 is only transport; the hash is over the decoded
/// bytes so it's canonical regardless of encoding.
/// </summary>
public static class WalletPin
{
    public static byte[] Compute(byte[] publicKeySpki) => SHA256.HashData(publicKeySpki);

    public static byte[] Compute(string publicKeyBase64) =>
        Compute(Convert.FromBase64String(publicKeyBase64));
}

/// <summary>
/// Loads a <see cref="WalletFile"/> and reports how its public key compares
/// against <see cref="IPinStore"/>. Policy (refuse / warn / enroll) is left
/// to the caller -- an interactive unlock refuses on
/// <see cref="PinCheck.Mismatch"/>, a read-only "show public key" only warns.
/// </summary>
public static class PinnedWallet
{
    public readonly record struct Result(WalletFile Wallet, PinCheck Check, byte[] ActualPin);

    public static Result Load(string walletPath, string walletId, IPinStore store)
    {
        var wallet = WalletFile.Load(walletPath);
        var actual = WalletPin.Compute(wallet.PublicKeyBase64);
        var pinned = store.TryGet(walletId);

        var check = pinned is null
            ? PinCheck.FirstUse
            : CryptographicOperations.FixedTimeEquals(pinned, actual)
                ? PinCheck.Match
                : PinCheck.Mismatch;

        KeyWalletDiagnostics.PinChecks.Add(1, new KeyValuePair<string, object?>(
            KeyWalletDiagnostics.TagPinResult,
            check switch
            {
                PinCheck.Match => "match",
                PinCheck.Mismatch => "mismatch",
                _ => "first_use"
            }));

        return new Result(wallet, check, actual);
    }
}
