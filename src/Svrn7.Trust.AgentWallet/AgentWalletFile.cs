using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// The on-disk representation of <c>agent-identity.wallet</c> (docs/AGENTWALLET.md
/// §7): a cleartext header plus a DEK-sealed payload blob and one
/// <see cref="WalletKeySlot"/> per unlock method (Version 2 — the current
/// format). A random Data Encryption Key (DEK) seals the payload exactly once;
/// each key slot wraps that same DEK under a different credential-derived
/// key, so adding a future unlock method (BACKLOG TDA-020) only adds a slot —
/// the payload ciphertext and every other slot are untouched.
///
/// Version 1 (legacy: a single Argon2id-sealed blob, password-only, no key
/// slots) can still be <em>read</em> — <see cref="Decrypt"/> falls back to it —
/// so an old wallet keeps working. <see cref="AgentWalletService.Unlock"/>
/// upgrades a v1 file to v2 in place the next time its password is used
/// successfully; every write path (<see cref="Encrypt"/>) always produces v2.
///
/// Atomic save adapted from the now-retired <c>Svrn7.Trust.KeyWallet</c>'s
/// <c>WalletFile</c>: serialize to a sibling ".tmp", flush to disk, then swap
/// into place, keeping the previous contents at ".bak".
/// </summary>
public sealed class AgentWalletFile
{
    /// <summary>AgentWallet on-disk format version. 1 = legacy single Argon2id+AES-256-GCM blob (read-only support). 2 = DEK envelope with key slots — the only version ever written.</summary>
    [JsonPropertyName("version")] public int Version { get; set; } = 2;

    /// <summary>secp256k1 compressed identity public key, hex — cleartext, so pinning and directory discovery work without unlocking.</summary>
    [JsonPropertyName("secp256k1PublicKeyHex")] public string Secp256k1PublicKeyHex { get; set; } = "";

    /// <summary>v1 only: Base64 of <see cref="WalletCrypto.EncryptV2"/> output over the payload UTF-8 bytes, sealed directly under the password.</summary>
    [JsonPropertyName("encryptedPayloadBase64")] public string? EncryptedPayloadBase64 { get; set; }

    /// <summary>v2 only: Base64 of <see cref="WalletCrypto.EncryptRaw"/> output over the payload UTF-8 bytes, sealed under the DEK.</summary>
    [JsonPropertyName("dekEncryptedPayloadBase64")] public string? DekEncryptedPayloadBase64 { get; set; }

    /// <summary>v2 only: one wrapped copy of the DEK per unlock method.</summary>
    [JsonPropertyName("keySlots")] public List<WalletKeySlot> KeySlots { get; set; } = new();

    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── payload -> file (always v2) ─────────────────────────────────────────

    /// <summary>
    /// Writes the current (v2) format: a fresh random DEK seals the payload
    /// once; a <c>"password"</c> slot always wraps it, and — since every
    /// payload written by <see cref="AgentWalletService.Create"/> carries a
    /// recovery phrase — a <c>"recoveryPhrase"</c> slot wraps it too. Every
    /// call generates a brand-new DEK, so a password change (or a v1→v2
    /// migration) also invalidates any previously-wrapped copy.
    /// </summary>
    internal static AgentWalletFile Encrypt(AgentWalletPayload payload, char[] password)
    {
        var dek = WalletCrypto.GenerateKey();
        try
        {
            var payloadBlob = WalletCrypto.EncryptRaw(dek, payload.ToUtf8());

            var slots = new List<WalletKeySlot> { BuildPasswordSlot(dek, password) };
            if (!string.IsNullOrWhiteSpace(payload.RecoveryPhrase))
                slots.Add(BuildRecoveryPhraseSlot(dek, payload.RecoveryPhrase));

            return new AgentWalletFile
            {
                Version = 2,
                Secp256k1PublicKeyHex = payload.Secp256k1PublicKeyHex,
                DekEncryptedPayloadBase64 = Convert.ToBase64String(payloadBlob),
                KeySlots = slots,
                CreatedUtc = payload.CreatedUtc
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static WalletKeySlot BuildPasswordSlot(byte[] dek, char[] password)
    {
        var (memoryKiB, iterations, parallelism) = WalletCrypto.DefaultArgon2Params;
        var salt = WalletCrypto.NewSalt();
        var kek = WalletCrypto.DeriveKeyArgon2id(password, salt, memoryKiB, iterations, parallelism);
        try
        {
            return new WalletKeySlot
            {
                Method = "password",
                SaltBase64 = Convert.ToBase64String(salt),
                Argon2MemoryKiB = memoryKiB,
                Argon2Iterations = iterations,
                Argon2Parallelism = parallelism,
                WrappedDekBase64 = Convert.ToBase64String(WalletCrypto.EncryptRaw(kek, dek))
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static WalletKeySlot BuildRecoveryPhraseSlot(byte[] dek, string recoveryPhrase)
    {
        var kek = RecoveryPhrase.DeriveWalletUnlockKey(recoveryPhrase);
        try
        {
            return new WalletKeySlot
            {
                Method = "recoveryPhrase",
                WrappedDekBase64 = Convert.ToBase64String(WalletCrypto.EncryptRaw(kek, dek))
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    // ── file -> payload ──────────────────────────────────────────────────────

    /// <summary>
    /// Decrypts the payload using the password. Handles the current (v2)
    /// DEK-envelope format; falls back to a legacy v1 file (a single
    /// Argon2id-sealed blob) so an old wallet can still be opened — the
    /// caller (<see cref="AgentWalletService.Unlock"/>) upgrades it to v2 in
    /// place on a successful v1 read. Throws <see cref="CryptographicException"/>
    /// on a wrong password (GCM tag check fails).
    /// </summary>
    internal AgentWalletPayload Decrypt(char[] password)
    {
        if (Version == 1)
            return DecryptLegacyV1(password);
        if (Version != 2)
            throw new InvalidDataException($"Unsupported wallet file version: {Version}");

        var slot = KeySlots.FirstOrDefault(s => s.Method == "password")
            ?? throw new InvalidDataException("Wallet file has no password key slot.");
        var salt = Convert.FromBase64String(slot.SaltBase64!);
        var kek = WalletCrypto.DeriveKeyArgon2id(
            password, salt,
            slot.Argon2MemoryKiB!.Value, slot.Argon2Iterations!.Value, slot.Argon2Parallelism!.Value);
        try
        {
            var dek = WalletCrypto.DecryptRaw(kek, Convert.FromBase64String(slot.WrappedDekBase64)); // CryptographicException on wrong password
            try
            {
                return DecryptPayloadWithDek(dek);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Decrypts the payload using a recovery phrase instead of the password —
    /// the phrase deterministically re-derives the same key that sealed the
    /// <c>"recoveryPhrase"</c> slot. Throws <see cref="FormatException"/> if
    /// <paramref name="phrase"/> is not a well-formed 12-word BIP39 phrase,
    /// <see cref="RecoveryPhraseSlotUnavailableException"/> if this wallet has
    /// no recovery-phrase slot (v1, not yet migrated), or
    /// <see cref="CryptographicException"/> if the phrase does not match.
    /// </summary>
    internal AgentWalletPayload DecryptWithRecoveryPhrase(string phrase)
    {
        RecoveryPhrase.Validate(phrase);
        if (Version == 1)
            throw new RecoveryPhraseSlotUnavailableException();
        if (Version != 2)
            throw new InvalidDataException($"Unsupported wallet file version: {Version}");

        var slot = KeySlots.FirstOrDefault(s => s.Method == "recoveryPhrase")
            ?? throw new RecoveryPhraseSlotUnavailableException();
        var kek = RecoveryPhrase.DeriveWalletUnlockKey(phrase);
        try
        {
            var dek = WalletCrypto.DecryptRaw(kek, Convert.FromBase64String(slot.WrappedDekBase64)); // CryptographicException on wrong phrase
            try
            {
                return DecryptPayloadWithDek(dek);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private AgentWalletPayload DecryptPayloadWithDek(byte[] dek)
    {
        var payloadUtf8 = WalletCrypto.DecryptRaw(dek, Convert.FromBase64String(DekEncryptedPayloadBase64!));
        try
        {
            return AgentWalletPayload.FromUtf8(payloadUtf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadUtf8);
        }
    }

    /// <summary>Legacy v1 read path — a single Argon2id-sealed blob, password-only, no key slots. Unchanged from the original AgentWallet format.</summary>
    private AgentWalletPayload DecryptLegacyV1(char[] password)
    {
        var blob = Convert.FromBase64String(EncryptedPayloadBase64!);
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
