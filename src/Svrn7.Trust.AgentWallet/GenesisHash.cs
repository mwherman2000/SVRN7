namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// The DID genesis hash: <c>Blake3(secp256k1 compressed public key bytes)</c>,
/// lowercase hex (64 chars). Derived once from the identity key pair and never
/// changes — role transitions update the DID Document, not this value. Also the
/// basis for the runtime folder slug (docs/AGENTWALLET.md §D2).
/// </summary>
public static class GenesisHash
{
    public static string Compute(byte[] compressedPublicKey) =>
        Blake3.Hasher.Hash(compressedPublicKey).ToString();

    public static string Compute(string compressedPublicKeyHex) =>
        Compute(Convert.FromHexString(compressedPublicKeyHex));

    /// <summary>First <paramref name="length"/> hex chars of the genesis hash — the folder-slug suffix (default 8).</summary>
    public static string Slug(string compressedPublicKeyHex, int length = 8) =>
        Compute(compressedPublicKeyHex)[..length];
}
