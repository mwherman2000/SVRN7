using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// Password → key derivation, and AES-256-GCM encrypt/decrypt of an arbitrary
/// byte payload. This is the "protecting" half of the wallet, copied from
/// <c>Svrn7.Trust.KeyWallet.WalletCrypto</c> (see docs/AGENTWALLET.md §3) with
/// only the namespace changed, so the blob format is identical and equally
/// battle-tested.
///
/// AgentWallet only ever writes the Argon2id ("V2") format:
///   memoryKiB(4) ‖ iterations(4) ‖ parallelism(4) ‖ salt(16) ‖ nonce(12) ‖
///   tag(16) ‖ ciphertext
/// The cost parameters are embedded in the blob, so a future cost bump does not
/// need a new format version. The PBKDF2 ("V1") path is retained for lineage
/// with KeyWallet but is not used by AgentWallet.
/// </summary>
public static class WalletCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard nonce size
    private const int TagSize = 16;
    private const int KeySize = 32; // AES-256
    private const int Pbkdf2Iterations = 600_000;

    // Argon2id defaults for newly-created / re-encrypted wallets. Comparable to
    // common password-manager defaults for a desktop app.
    private const int Argon2MemoryKiB = 65536; // 64 MiB
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 4;
    private const int Argon2HeaderSize = 12; // memoryKiB(4) + iterations(4) + parallelism(4)

    public static byte[] DeriveKeyPbkdf2(char[] password, byte[] salt)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                password: passwordBytes,
                salt: salt,
                iterations: Pbkdf2Iterations,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static byte[] DeriveKeyArgon2id(char[] password, byte[] salt, int memoryKiB, int iterations, int parallelism)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = parallelism,
                Iterations = iterations,
                MemorySize = memoryKiB
            };
            return argon2.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    /// <summary>
    /// Derives the Argon2id key with AgentWallet's default cost parameters,
    /// against a fresh salt. Returned as <c>(key, salt)</c>; the caller must
    /// zero <c>key</c>. Used for the DB-master-key wrap, which shares the wallet
    /// password but is a separate AES-GCM operation from the payload.
    /// </summary>
    public static (byte[] Key, byte[] Salt) DeriveDefaultKey(char[] password)
    {
        var salt = NewSalt();
        var key = DeriveKeyArgon2id(password, salt, Argon2MemoryKiB, Argon2Iterations, Argon2Parallelism);
        return (key, salt);
    }

    /// <summary>Re-derives the Argon2id key for a known salt (default cost parameters).</summary>
    public static byte[] DeriveDefaultKey(char[] password, byte[] salt) =>
        DeriveKeyArgon2id(password, salt, Argon2MemoryKiB, Argon2Iterations, Argon2Parallelism);

    /// <summary>Encrypts under PBKDF2-SHA256/600k — the legacy (V1) format. Retained for lineage; unused by AgentWallet.</summary>
    public static byte[] EncryptV1(byte[] plaintext, char[] password)
    {
        var salt = NewSalt();
        var key = DeriveKeyPbkdf2(password, salt);
        return EncryptWithKey(plaintext, key, prefix: salt);
    }

    /// <summary>Reverses <see cref="EncryptV1"/>. Throws <see cref="CryptographicException"/> on wrong password or tampered data.</summary>
    public static byte[] DecryptV1(byte[] blob, char[] password)
    {
        var salt = blob[0..SaltSize];
        var rest = blob[SaltSize..];
        var key = DeriveKeyPbkdf2(password, salt);
        return DecryptWithKey(rest, key);
    }

    /// <summary>Encrypts under Argon2id — the current (V2) format. See the class doc-comment for the blob layout.</summary>
    public static byte[] EncryptV2(byte[] plaintext, char[] password)
    {
        var salt = NewSalt();
        var key = DeriveKeyArgon2id(password, salt, Argon2MemoryKiB, Argon2Iterations, Argon2Parallelism);

        var header = new byte[Argon2HeaderSize];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), Argon2MemoryKiB);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), Argon2Iterations);
        BitConverter.TryWriteBytes(header.AsSpan(8, 4), Argon2Parallelism);

        var encrypted = EncryptWithKey(plaintext, key, prefix: salt);
        var result = new byte[header.Length + encrypted.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(encrypted, 0, result, header.Length, encrypted.Length);
        return result;
    }

    /// <summary>Reverses <see cref="EncryptV2"/>. Throws <see cref="CryptographicException"/> on wrong password or tampered data.</summary>
    public static byte[] DecryptV2(byte[] blob, char[] password)
    {
        var memoryKiB = BitConverter.ToInt32(blob, 0);
        var iterations = BitConverter.ToInt32(blob, 4);
        var parallelism = BitConverter.ToInt32(blob, 8);
        var rest = blob[Argon2HeaderSize..];

        var salt = rest[0..SaltSize];
        var nonceTagCiphertext = rest[SaltSize..];
        var key = DeriveKeyArgon2id(password, salt, memoryKiB, iterations, parallelism);
        return DecryptWithKey(nonceTagCiphertext, key);
    }

    /// <summary>Returns <c>prefix ‖ nonce ‖ tag ‖ ciphertext</c>. Zeroes <paramref name="key"/> before returning.</summary>
    public static byte[] EncryptWithKey(byte[] plaintext, byte[] key, byte[] prefix)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var result = new byte[prefix.Length + NonceSize + TagSize + ciphertext.Length];
        var offset = 0;
        Buffer.BlockCopy(prefix, 0, result, offset, prefix.Length); offset += prefix.Length;
        Buffer.BlockCopy(nonce, 0, result, offset, NonceSize); offset += NonceSize;
        Buffer.BlockCopy(tag, 0, result, offset, TagSize); offset += TagSize;
        Buffer.BlockCopy(ciphertext, 0, result, offset, ciphertext.Length);
        return result;
    }

    /// <summary>
    /// Expects <paramref name="nonceTagCiphertext"/> = <c>nonce ‖ tag ‖
    /// ciphertext</c> (salt/header already stripped by the caller). Zeroes
    /// <paramref name="key"/> before returning.
    /// </summary>
    public static byte[] DecryptWithKey(byte[] nonceTagCiphertext, byte[] key)
    {
        var nonce = nonceTagCiphertext[0..NonceSize];
        var tag = nonceTagCiphertext[NonceSize..(NonceSize + TagSize)];
        var ciphertext = nonceTagCiphertext[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return plaintext;
    }
}
