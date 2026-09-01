using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// BIP39-style recovery phrase support: 12-word mnemonic (128-bit entropy)
/// with checksum, and BIP39-standard seed derivation (PBKDF2-HMAC-SHA512,
/// 2048 iterations, "mnemonic"+passphrase salt).
///
/// SCOPE NOTE: this follows the standard BIP39 mnemonic<->seed steps, but
/// how the resulting 64-byte seed becomes a P-256 private key
/// (KeyPair.FromSeed / EcMath.DeriveScalarFromSeed) is this wallet's own
/// construction, not a published standard (BIP32 HD derivation is defined
/// for secp256k1, not P-256). A phrase generated here will recover the key
/// in THIS wallet app, but won't import into a standard BIP32/44 wallet.
/// </summary>
public static class Mnemonic
{
    private const int EntropyBits = 128; // -> 12 words
    private const int ChecksumBits = EntropyBits / 32; // 4 bits
    private const int WordCount = (EntropyBits + ChecksumBits) / 11; // 12

    private static string[]? _wordList;

    private static string[] WordList
    {
        get
        {
            if (_wordList is not null) return _wordList;

            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("wordlist_english.txt")
                ?? throw new InvalidOperationException("Embedded BIP39 wordlist resource not found.");
            using var reader = new StreamReader(stream);
            var words = reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (words.Length != 2048)
                throw new InvalidOperationException($"BIP39 wordlist should have 2048 words, found {words.Length}.");

            _wordList = words;
            return _wordList;
        }
    }

    public static string Generate()
    {
        var entropy = RandomNumberGenerator.GetBytes(EntropyBits / 8); // 16 bytes
        return EntropyToMnemonic(entropy);
    }

    private static string EntropyToMnemonic(byte[] entropy)
    {
        var checksumByte = SHA256.HashData(entropy)[0];

        // Build a bit string: entropy bits followed by the top ChecksumBits of the hash byte.
        var bits = new bool[EntropyBits + ChecksumBits];
        for (var i = 0; i < entropy.Length; i++)
            for (var b = 0; b < 8; b++)
                bits[i * 8 + b] = (entropy[i] & (1 << (7 - b))) != 0;

        for (var b = 0; b < ChecksumBits; b++)
            bits[EntropyBits + b] = (checksumByte & (1 << (7 - b))) != 0;

        var words = new string[WordCount];
        var list = WordList;
        for (var w = 0; w < WordCount; w++)
        {
            var index = 0;
            for (var b = 0; b < 11; b++)
            {
                index <<= 1;
                if (bits[w * 11 + b]) index |= 1;
            }
            words[w] = list[index];
        }

        return string.Join(' ', words);
    }

    /// <summary>Validates word count, word membership, and checksum. Throws with a specific reason if invalid.</summary>
    public static void Validate(string mnemonic)
    {
        var words = mnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != WordCount)
            throw new FormatException($"Recovery phrase must be exactly {WordCount} words (got {words.Length}).");

        var list = WordList;
        var bits = new bool[EntropyBits + ChecksumBits];
        for (var w = 0; w < WordCount; w++)
        {
            var index = Array.IndexOf(list, words[w]);
            if (index < 0)
                throw new FormatException($"'{words[w]}' is not a valid recovery-phrase word.");

            for (var b = 0; b < 11; b++)
                bits[w * 11 + b] = (index & (1 << (10 - b))) != 0;
        }

        var entropy = new byte[EntropyBits / 8];
        for (var i = 0; i < entropy.Length; i++)
        {
            byte value = 0;
            for (var b = 0; b < 8; b++)
                if (bits[i * 8 + b]) value |= (byte)(1 << (7 - b));
            entropy[i] = value;
        }

        var expectedChecksumByte = SHA256.HashData(entropy)[0];
        byte actualChecksum = 0;
        for (var b = 0; b < ChecksumBits; b++)
            if (bits[EntropyBits + b]) actualChecksum |= (byte)(1 << (ChecksumBits - 1 - b));

        var expectedTopBits = (byte)(expectedChecksumByte >> (8 - ChecksumBits));
        if (actualChecksum != expectedTopBits)
            throw new FormatException("Recovery phrase checksum is invalid -- check the word order and spelling.");
    }

    /// <summary>BIP39-standard seed derivation: PBKDF2-HMAC-SHA512(mnemonic, "mnemonic"+passphrase, 2048 iterations, 64 bytes).</summary>
    public static byte[] ToSeed(string mnemonic, string passphrase = "")
    {
        var normalizedMnemonic = mnemonic.Trim().Normalize(NormalizationForm.FormKD);
        var normalizedSalt = ("mnemonic" + passphrase).Normalize(NormalizationForm.FormKD);

        return Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(normalizedMnemonic),
            salt: Encoding.UTF8.GetBytes(normalizedSalt),
            iterations: 2048,
            hashAlgorithm: HashAlgorithmName.SHA512,
            outputLength: 64);
    }
}
