using System.Security.Cryptography;

namespace Svrn7.Trust.AgentWallet;

/// <summary>Result of comparing a loaded wallet's public key against the pin store.</summary>
public enum PinCheck
{
    /// <summary>The wallet's public key matches the pinned value.</summary>
    Match,

    /// <summary>Nothing is pinned for this wallet yet (trust-on-first-use), or pinning is disabled.</summary>
    FirstUse,

    /// <summary>A pin exists and the wallet's public key does NOT match it — the file was replaced or rolled back.</summary>
    Mismatch
}

/// <summary>
/// Computes the pin value for a wallet's identity key. The pin is SHA-256 of the
/// raw secp256k1 <b>compressed</b> public key bytes (33 bytes) — a public key is
/// not secret, so what is wanted is a short fixed-size fingerprint to compare,
/// not confidentiality. Hex is only transport; the hash is over the decoded
/// bytes so it is canonical regardless of casing.
/// </summary>
public static class WalletPin
{
    public static byte[] Compute(byte[] compressedPublicKey) => SHA256.HashData(compressedPublicKey);

    public static byte[] Compute(string compressedPublicKeyHex) =>
        Compute(Convert.FromHexString(compressedPublicKeyHex));
}
