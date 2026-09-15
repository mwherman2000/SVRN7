using System.Security.Cryptography;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// An unlocked TDA agent identity: the decrypted key material plus the identity
/// metadata from the wallet payload. The caller owns this and must dispose it;
/// <see cref="Dispose"/> zeroes the three private-key / key arrays. The recovery
/// phrase is not held here — call <see cref="AgentWalletService.ExportRecoveryPhrase"/>
/// when it is actually needed.
/// </summary>
public sealed class AgentIdentity : IDisposable
{
    /// <summary>secp256k1 signing private key, 32 bytes — <c>AgentSigningPrivateKey</c> (outbound JWS).</summary>
    public byte[] Secp256k1PrivateKey { get; }

    /// <summary>secp256k1 compressed public key, 66 hex.</summary>
    public string Secp256k1PublicKeyHex { get; }

    /// <summary>X25519 key-agreement private key, 32 bytes — <c>AgentKeyAgreementPrivateKey</c> (inbound JWE decrypt).</summary>
    public byte[] X25519PrivateKey { get; }

    /// <summary>X25519 public key, 64 hex.</summary>
    public string X25519PublicKeyHex { get; }

    /// <summary>The 32-byte LiteDB master key; hex(this) is the LiteDB <c>Password=</c> for every database.</summary>
    public byte[] DbMasterKey { get; }

    public string Did { get; }
    public string Role { get; }
    /// <summary>Parent-tier DID (Society/Federation) — a routing pointer. The parent's
    /// endpoint is never stored; it is resolved from the parent's DID Document at startup.</summary>
    public string? ParentTdaDid { get; }

    /// <summary>
    /// <see cref="Blake3"/> hash of the compressed secp256k1 public key, lowercase
    /// hex (64 chars). The DID genesis hash; also the basis for the runtime
    /// folder slug.
    /// </summary>
    public string GenesisHashHex { get; }

    private bool _disposed;

    internal AgentIdentity(
        byte[] secp256k1PrivateKey, string secp256k1PublicKeyHex,
        byte[] x25519PrivateKey, string x25519PublicKeyHex,
        byte[] dbMasterKey,
        string did, string role, string? parentTdaDid,
        string genesisHashHex)
    {
        Secp256k1PrivateKey = secp256k1PrivateKey;
        Secp256k1PublicKeyHex = secp256k1PublicKeyHex;
        X25519PrivateKey = x25519PrivateKey;
        X25519PublicKeyHex = x25519PublicKeyHex;
        DbMasterKey = dbMasterKey;
        Did = did;
        Role = role;
        ParentTdaDid = parentTdaDid;
        GenesisHashHex = genesisHashHex;
    }

    /// <summary>hex(<see cref="DbMasterKey"/>) — the LiteDB connection-string <c>Password=</c> value.</summary>
    public string DatabasePassword() =>
        Convert.ToHexString(ThrowIfDisposed().DbMasterKey).ToLowerInvariant();

    private AgentIdentity ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return this;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(Secp256k1PrivateKey);
        CryptographicOperations.ZeroMemory(X25519PrivateKey);
        CryptographicOperations.ZeroMemory(DbMasterKey);
        _disposed = true;
    }
}
