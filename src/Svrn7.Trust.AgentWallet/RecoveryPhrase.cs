using System.Security.Cryptography;
using NBitcoin;
using NSec.Cryptography;
using NBitcoinKey = NBitcoin.Key;
using NsecKey = NSec.Cryptography.Key;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// BIP39 recovery phrase and deterministic key derivation for a TDA agent
/// identity (docs/AGENTWALLET.md §D10).
///
/// <list type="bullet">
///   <item>12-word / 128-bit BIP39 mnemonic (<see cref="NBitcoin"/>).</item>
///   <item>The <b>secp256k1</b> identity key is BIP32-derived at the Web7-owned
///     path <c>m/7'/0'/0'/0/0</c> (purpose 7' — deliberately not SLIP-0044
///     registered; phrases are Web7-internal).</item>
///   <item>The <b>X25519</b> key-agreement key is re-derived from the same BIP39
///     seed via <c>HKDF-SHA256(seed, info = "web7-pando/x25519/v1")</c> then
///     RFC 7748-clamped, so one phrase restores both keys.</item>
/// </list>
/// </summary>
public static class RecoveryPhrase
{
    /// <summary>BIP32 derivation path for the secp256k1 identity key. Permanent.</summary>
    public static readonly KeyPath IdentityKeyPath = new("7'/0'/0'/0/0");

    private static readonly byte[] X25519Info = "web7-pando/x25519/v1"u8.ToArray();

    private const int EntropyBytes = 16; // 128-bit → 12 words

    /// <summary>A fresh random 12-word phrase. Not persisted by this method — the caller stores it in the encrypted wallet payload.</summary>
    public static string Generate()
    {
        var entropy = RandomNumberGenerator.GetBytes(EntropyBytes);
        try
        {
            return new Mnemonic(Wordlist.English, entropy).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>
    /// Validates word count, word membership, and checksum.
    /// </summary>
    /// <exception cref="FormatException">The phrase is not a valid 12-word BIP39 English mnemonic.</exception>
    public static void Validate(string phrase)
    {
        Mnemonic mnemonic;
        try
        {
            mnemonic = new Mnemonic(phrase, Wordlist.English);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException("Recovery phrase is not a valid BIP39 English mnemonic.", ex);
        }

        var wordCount = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (wordCount != 12)
            throw new FormatException($"Recovery phrase must be exactly 12 words (got {wordCount}).");
        if (!mnemonic.IsValidChecksum)
            throw new FormatException("Recovery phrase checksum is invalid — check the word order and spelling.");
    }

    /// <summary>
    /// Deterministically derives both keys from <paramref name="phrase"/>.
    /// Same phrase (+ passphrase) always yields the same keys — that is the
    /// recovery property. The caller owns the returned <see cref="DerivedKeys"/>
    /// and must dispose it.
    /// </summary>
    /// <exception cref="FormatException">The phrase is invalid.</exception>
    public static DerivedKeys Derive(string phrase, string? passphrase = null)
    {
        Validate(phrase);
        var mnemonic = new Mnemonic(phrase, Wordlist.English);

        byte[]? seed = null;
        try
        {
            seed = mnemonic.DeriveSeed(passphrase);

            // secp256k1 — BIP32 at m/7'/0'/0'/0/0
            var root = ExtKey.CreateFromSeed(seed);
            NBitcoinKey identityKey = root.Derive(IdentityKeyPath).PrivateKey;
            var secpPriv = identityKey.ToBytes();                    // 32 bytes
            var secpPubHex = identityKey.PubKey.Compress().ToHex();  // 66 hex

            // X25519 — HKDF from the same seed, then RFC 7748 clamp
            var xPriv = HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, 32, salt: null, info: X25519Info);
            xPriv[0] &= 0xF8;
            xPriv[31] &= 0x7F;
            xPriv[31] |= 0x40;

            using var nsecKey = NsecKey.Import(
                KeyAgreementAlgorithm.X25519, xPriv, KeyBlobFormat.RawPrivateKey,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            var xPubHex = Convert.ToHexString(
                nsecKey.PublicKey.Export(KeyBlobFormat.RawPublicKey)).ToLowerInvariant(); // 64 hex

            return new DerivedKeys(secpPriv, secpPubHex, xPriv, xPubHex, phrase);
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
        }
    }
}

/// <summary>
/// The key material derived from a recovery phrase. Dispose zeroes the two
/// private-key arrays. The phrase string cannot be zeroed (immutable) — that is
/// an inherent BIP39 property.
/// </summary>
public sealed class DerivedKeys : IDisposable
{
    public byte[] Secp256k1PrivateKey { get; }
    public string Secp256k1PublicKeyHex { get; }
    public byte[] X25519PrivateKey { get; }
    public string X25519PublicKeyHex { get; }
    public string Phrase { get; }

    private bool _disposed;

    internal DerivedKeys(
        byte[] secp256k1PrivateKey, string secp256k1PublicKeyHex,
        byte[] x25519PrivateKey, string x25519PublicKeyHex, string phrase)
    {
        Secp256k1PrivateKey = secp256k1PrivateKey;
        Secp256k1PublicKeyHex = secp256k1PublicKeyHex;
        X25519PrivateKey = x25519PrivateKey;
        X25519PublicKeyHex = x25519PublicKeyHex;
        Phrase = phrase;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(Secp256k1PrivateKey);
        CryptographicOperations.ZeroMemory(X25519PrivateKey);
        _disposed = true;
    }
}
