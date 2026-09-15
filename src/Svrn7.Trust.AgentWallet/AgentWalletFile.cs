using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// The on-disk representation of <c>agent-identity.wallet</c> (docs/AGENTWALLET.md
/// §7): a cleartext header plus one AES-256-GCM blob. The blob is an
/// <see cref="AgentWalletPayload"/> sealed under the Argon2id password key; the
/// plaintext payload never touches disk.
///
/// Atomic save adapted from the now-retired <c>Svrn7.Trust.KeyWallet</c>'s
/// <c>WalletFile</c>: serialize to a sibling ".tmp", flush to disk, then swap
/// into place, keeping the previous contents at ".bak".
/// </summary>
public sealed class AgentWalletFile
{
    /// <summary>AgentWallet on-disk format version. 1 = Argon2id + AES-256-GCM.</summary>
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>secp256k1 compressed identity public key, hex — cleartext, so pinning and directory discovery work without unlocking.</summary>
    [JsonPropertyName("secp256k1PublicKeyHex")] public string Secp256k1PublicKeyHex { get; set; } = "";

    /// <summary>Base64 of <see cref="WalletCrypto.EncryptV2"/> output over the payload UTF-8 bytes.</summary>
    [JsonPropertyName("encryptedPayloadBase64")] public string EncryptedPayloadBase64 { get; set; } = "";

    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── payload <-> file ────────────────────────────────────────────────────

    internal static AgentWalletFile Encrypt(AgentWalletPayload payload, char[] password)
    {
        var blob = WalletCrypto.EncryptV2(payload.ToUtf8(), password);
        return new AgentWalletFile
        {
            Version = 1,
            Secp256k1PublicKeyHex = payload.Secp256k1PublicKeyHex,
            EncryptedPayloadBase64 = Convert.ToBase64String(blob),
            CreatedUtc = payload.CreatedUtc
        };
    }

    /// <summary>
    /// Decrypts the payload. Throws <see cref="CryptographicException"/> on a
    /// wrong password (GCM tag check fails) — a deliberate hard failure, not a
    /// silent wrong-key return.
    /// </summary>
    internal AgentWalletPayload Decrypt(char[] password)
    {
        if (Version != 1)
            throw new InvalidDataException($"Unsupported wallet file version: {Version}");

        var blob = Convert.FromBase64String(EncryptedPayloadBase64);
        var utf8 = WalletCrypto.DecryptV2(blob, password);
        try
        {
            return AgentWalletPayload.FromUtf8(utf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    // ── file IO ────────────────────────────────────────────────────────────

    public static AgentWalletFile Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AgentWalletFile>(json, JsonOpts)
                ?? throw new InvalidDataException("Wallet file could not be parsed.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var backupPath = path + ".bak";
            var hint = File.Exists(backupPath)
                ? $" A pre-write backup exists at '{backupPath}' — inspect it before restoring, since it reflects an older password/key."
                : "";
            throw new InvalidDataException($"Wallet file at '{path}' is corrupted or not valid JSON.{hint}", ex);
        }
    }

    /// <summary>
    /// Writes atomically: serialize to "<c>.tmp</c>", flush to disk, then swap
    /// into place. If <paramref name="path"/> exists, the previous contents are
    /// kept at "<c>.bak</c>" via <see cref="File.Replace(string,string,string)"/>.
    /// </summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var json = JsonSerializer.Serialize(this, JsonOpts);
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
                File.Copy(path, backupPath, overwrite: true);
                File.Move(tempPath, path, overwrite: true);
            }
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
