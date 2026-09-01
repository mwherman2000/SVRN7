using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// Password -> key derivation, and AES-256-GCM encrypt/decrypt of the
/// raw private key bytes. This is the "protecting" half of the wallet.
///
/// Two KDFs are supported, selected by WalletFile.Version so existing
/// wallets keep working unchanged after this file's Argon2id upgrade:
///   Version 1 (legacy): PBKDF2-SHA256, 600,000 iterations, fixed params,
///   blob = salt(16) || nonce(12) || tag(16) || ciphertext.
///   Version 2 (current default for new/re-encrypted wallets): Argon2id,
///   with its cost parameters embedded in the blob itself (memoryKiB(4) ||
///   iterations(4) || parallelism(4) || salt(16) || nonce(12) || tag(16) ||
///   ciphertext) so a future cost bump doesn't require a Version 3.
/// </summary>
public static class WalletCrypto
{
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard nonce size
    private const int TagSize = 16;
    private const int KeySize = 32; // AES-256
    private const int Pbkdf2Iterations = 600_000;

    // Argon2id defaults for newly-created/re-encrypted (Version 2) wallets.
    // Comparable to common password-manager defaults for a desktop app.
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

    public static byte[] NewSalt()
    {
        return RandomNumberGenerator.GetBytes(SaltSize);
    }

    /// <summary>
    /// Encrypts under PBKDF2-SHA256/600k -- the Version 1 (legacy) format.
    /// Byte-for-byte compatible with existing wallet.json files; do not
    /// change this format, only DecryptV1/EncryptV1's callers should change.
    /// </summary>
    public static byte[] EncryptV1(byte[] plaintext, char[] password)
    {
        var salt = NewSalt();
        var key = DeriveKeyPbkdf2(password, salt);
        return EncryptWithKey(plaintext, key, prefix: salt);
    }

    /// <summary>Reverses EncryptV1(). Throws CryptographicException on wrong password or tampered data.</summary>
    public static byte[] DecryptV1(byte[] blob, char[] password)
    {
        var salt = blob[0..SaltSize];
        var rest = blob[SaltSize..];
        var key = DeriveKeyPbkdf2(password, salt);
        return DecryptWithKey(rest, key);
    }

    /// <summary>Encrypts under Argon2id -- the Version 2 (current) format. See class doc-comment for the blob layout.</summary>
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

    /// <summary>Reverses EncryptV2(). Throws CryptographicException on wrong password or tampered data.</summary>
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

    /// <summary>Returns prefix || nonce || tag || ciphertext. Zeroes <paramref name="key"/> before returning.</summary>
    private static byte[] EncryptWithKey(byte[] plaintext, byte[] key, byte[] prefix)
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
    /// Expects <paramref name="nonceTagCiphertext"/> = nonce || tag ||
    /// ciphertext (salt/header already stripped by the caller). Zeroes
    /// <paramref name="key"/> before returning.
    /// </summary>
    private static byte[] DecryptWithKey(byte[] nonceTagCiphertext, byte[] key)
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
