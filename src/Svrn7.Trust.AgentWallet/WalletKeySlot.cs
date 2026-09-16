using System.Text.Json.Serialization;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// One way to unwrap a wallet's Data Encryption Key (DEK). The wallet payload
/// is encrypted exactly once, under the DEK (docs/AGENTWALLET.md §7); each key
/// slot wraps that same DEK under a different credential-derived
/// key-encryption key. Today: <c>"password"</c> (Argon2id) and
/// <c>"recoveryPhrase"</c> (HKDF from the BIP39 phrase). Adding a future
/// unlock method (e.g. device-bound authentication, BACKLOG TDA-020) means
/// adding a new slot value — the payload ciphertext and every other slot are
/// untouched.
/// </summary>
public sealed class WalletKeySlot
{
    [JsonPropertyName("method")] public string Method { get; set; } = "";

    /// <summary>Argon2id salt, base64 — <c>"password"</c> slot only.</summary>
    [JsonPropertyName("saltBase64")] public string? SaltBase64 { get; set; }

    /// <summary>
    /// Argon2id cost parameters used for this slot — <c>"password"</c> slot
    /// only. Embedded per-slot (mirroring <see cref="WalletCrypto.EncryptV2"/>'s
    /// blob header) so a future cost bump does not require a new file version.
    /// </summary>
    [JsonPropertyName("argon2MemoryKiB")] public int? Argon2MemoryKiB { get; set; }
    [JsonPropertyName("argon2Iterations")] public int? Argon2Iterations { get; set; }
    [JsonPropertyName("argon2Parallelism")] public int? Argon2Parallelism { get; set; }

    /// <summary>AES-256-GCM(KEK, DEK) — see <see cref="WalletCrypto.EncryptRaw"/>.</summary>
    [JsonPropertyName("wrappedDekBase64")] public string WrappedDekBase64 { get; set; } = "";
}
