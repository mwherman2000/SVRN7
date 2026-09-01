using System.Diagnostics;
using System.Text.Json;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// The on-disk representation of a wallet. The private key is always
/// stored encrypted (EncryptedPrivateKey); the plaintext private key
/// never touches disk.
/// </summary>
public sealed class WalletFile
{
    public int Version { get; set; } = 1;
    public string PublicKeyBase64 { get; set; } = "";

    // Base64 of: salt || nonce || tag || ciphertext (see WalletCrypto)
    public string EncryptedPrivateKeyBase64 { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Always writes the current (Version 2 / Argon2id) format. Existing
    /// Version 1 (PBKDF2) wallet files are left as-is until the user
    /// explicitly re-encrypts them (e.g. via "change password"), at which
    /// point they're transparently upgraded by going through this method.
    /// </summary>
    public static WalletFile Create(KeyPair keyPair, char[] password)
    {
        var encrypted = WalletCrypto.EncryptV2(keyPair.PrivateKeyPkcs8, password);
        return new WalletFile
        {
            Version = 2,
            PublicKeyBase64 = keyPair.PublicKeyBase64,
            EncryptedPrivateKeyBase64 = Convert.ToBase64String(encrypted),
            CreatedUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Writes atomically: serialize to a sibling ".tmp" file, flush it to
    /// disk, then swap it into place. If <paramref name="path"/> already
    /// exists, the previous contents are preserved at ".bak" (via
    /// File.Replace's atomic swap-with-backup) instead of being overwritten
    /// in place -- a crash mid-write can never leave a half-written or
    /// missing wallet file.
    /// </summary>
    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            try
            {
                File.Replace(tempPath, path, backupPath);
            }
            catch (PlatformNotSupportedException)
            {
                // File.Replace isn't available on every platform; fall back
                // to a manual (still effectively atomic on POSIX) backup + move.
                File.Copy(path, backupPath, overwrite: true);
                File.Move(tempPath, path, overwrite: true);
            }
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    public static WalletFile Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WalletFile>(json)
                ?? throw new InvalidDataException("Wallet file could not be parsed.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var backupPath = path + ".bak";
            var hint = File.Exists(backupPath)
                ? $" A pre-write backup exists at '{backupPath}' -- inspect it before manually restoring, since it reflects an older password/key."
                : "";
            throw new InvalidDataException($"Wallet file at '{path}' is corrupted or not valid JSON.{hint}", ex);
        }
    }

    /// <summary>
    /// Attempts to unlock the wallet with the given password.
    /// Throws System.Security.Cryptography.CryptographicException if the
    /// password is wrong (GCM auth tag check fails) -- this is a deliberate
    /// hard failure, not a silent wrong-key return.
    /// </summary>
    public KeyPair Unlock(char[] password)
    {
        var encrypted = Convert.FromBase64String(EncryptedPrivateKeyBase64);
        var kdf = Version switch { 1 => "pbkdf2", 2 => "argon2id", _ => "unknown" };
        var startTs = Stopwatch.GetTimestamp();

        byte[] privateKeyBytes;
        try
        {
            privateKeyBytes = Version switch
            {
                1 => WalletCrypto.DecryptV1(encrypted, password),
                2 => WalletCrypto.DecryptV2(encrypted, password),
                _ => throw new InvalidDataException($"Unsupported wallet file version: {Version}")
            };
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            RecordKdfDuration(startTs, kdf, KeyWalletResult.AuthFailed);
            throw;
        }

        RecordKdfDuration(startTs, kdf, KeyWalletResult.Success);
        try
        {
            return KeyPair.FromPrivateKey(privateKeyBytes);
        }
        finally
        {
            Array.Clear(privateKeyBytes);
        }
    }

    // kdf ("pbkdf2" / "argon2id") already implies the wallet version, so no
    // separate version tag here.
    private static void RecordKdfDuration(long startTimestamp, string kdf, string result) =>
        KeyWalletDiagnostics.UnlockKdfDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>(KeyWalletDiagnostics.TagKdf, kdf),
            new KeyValuePair<string, object?>(KeyWalletDiagnostics.TagResult, result));

}
