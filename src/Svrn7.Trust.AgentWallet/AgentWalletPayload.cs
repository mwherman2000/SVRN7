using System.Text.Json;
using System.Text.Json.Serialization;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// The plaintext of the wallet's encrypted blob (docs/AGENTWALLET.md §8). Never
/// written to disk unencrypted — <see cref="AgentWalletFile"/> AES-256-GCM-seals
/// it under the Argon2id password key before it touches the filesystem, so every
/// field here (private keys, recovery phrase, DB master key) is protected by the
/// one password-derived key and one KDF pass.
/// </summary>
internal sealed class AgentWalletPayload
{
    [JsonPropertyName("did")]                    public string Did { get; set; } = "";
    [JsonPropertyName("role")]                   public string Role { get; set; } = "";
    [JsonPropertyName("createdUtc")]             public string CreatedUtc { get; set; } = "";

    [JsonPropertyName("secp256k1PrivateKeyHex")] public string Secp256k1PrivateKeyHex { get; set; } = "";
    [JsonPropertyName("secp256k1PublicKeyHex")]  public string Secp256k1PublicKeyHex { get; set; } = "";
    [JsonPropertyName("x25519PrivateKeyHex")]    public string X25519PrivateKeyHex { get; set; } = "";
    [JsonPropertyName("x25519PublicKeyHex")]     public string X25519PublicKeyHex { get; set; } = "";

    [JsonPropertyName("parentTdaDid")]           public string? ParentTdaDid { get; set; }
    [JsonPropertyName("parentTdaEndpointUrl")]   public string? ParentTdaEndpointUrl { get; set; }

    /// <summary>The 12-word BIP39 phrase itself. Stored here (inside the sealed blob) rather than as raw entropy — equally secret, and no reconstruction step for <c>ExportRecoveryPhrase</c>.</summary>
    [JsonPropertyName("recoveryPhrase")]         public string RecoveryPhrase { get; set; } = "";
    [JsonPropertyName("bip39EntropyBits")]       public int Bip39EntropyBits { get; set; } = 128;

    /// <summary>
    /// The 32-byte LiteDB master key, hex. A stable random value: changing the
    /// wallet password re-seals this payload (cheap) but does not change this
    /// value, so the databases are never re-keyed (§D8). Rotating it is a
    /// separate explicit operation that rebuilds every database.
    /// </summary>
    [JsonPropertyName("dbMasterKeyHex")]         public string DbMasterKeyHex { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOpts);

    internal static AgentWalletPayload FromUtf8(byte[] utf8) =>
        JsonSerializer.Deserialize<AgentWalletPayload>(utf8, JsonOpts)
        ?? throw new InvalidDataException("Wallet payload could not be parsed after decryption.");
}
