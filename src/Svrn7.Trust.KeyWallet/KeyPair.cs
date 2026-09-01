using System.Numerics;
using System.Security.Cryptography;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// ECDSA P-256 key pair. Chosen because it needs zero external NuGet
/// dependencies (System.Security.Cryptography.ECDsa is in the BCL).
///
/// ASSUMPTION FLAGGED: if this needs to interop with did:key / did:drn
/// tooling elsewhere in your stack, you likely want Ed25519 (NSec.Cryptography
/// or similar) or secp256k1 (for Blockcore/SOVRONA alignment) instead.
/// This class is the only place key-algorithm-specific code lives, so
/// swapping it out shouldn't touch WalletCrypto or the console UX.
/// </summary>
public sealed class KeyPair : IDisposable
{
    public byte[] PrivateKeyPkcs8 { get; }
    public byte[] PublicKeySubjectPublicKeyInfo { get; }

    private bool _disposed;

    private KeyPair(byte[] privateKeyPkcs8, byte[] publicKeySpki)
    {
        PrivateKeyPkcs8 = privateKeyPkcs8;
        PublicKeySubjectPublicKeyInfo = publicKeySpki;
    }

    /// <summary>
    /// Zeroes the in-memory private key. After this, Sign() throws
    /// ObjectDisposedException instead of failing with a confusing
    /// CryptographicException from importing zeroed key bytes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(PrivateKeyPkcs8);
        _disposed = true;
    }

    public static KeyPair Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdsa.ExportPkcs8PrivateKey();
        var pub = ecdsa.ExportSubjectPublicKeyInfo();
        return new KeyPair(priv, pub);
    }

    public static KeyPair FromPrivateKey(byte[] privateKeyPkcs8)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        var pub = ecdsa.ExportSubjectPublicKeyInfo();
        // Own a private copy: callers (e.g. WalletFile.Unlock) may zero their
        // buffer right after this returns, and arrays are reference types.
        return new KeyPair((byte[])privateKeyPkcs8.Clone(), pub);
    }

    public byte[] Sign(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(PrivateKeyPkcs8, out _);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256);
    }

    public static bool Verify(byte[] publicKeySpki, byte[] data, byte[] signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public string PublicKeyBase64 => Convert.ToBase64String(PublicKeySubjectPublicKeyInfo);

    /// <summary>
    /// Deterministically derives a key pair from a 64-byte BIP39 seed.
    /// The private scalar is the seed's first 32 bytes reduced mod (n-1),
    /// plus 1, keeping it in the valid range [1, n-1]. Q = d*G is computed
    /// via EcMath (see that file's SelfTest for the correctness check).
    /// Same seed always yields the same key pair -- that's the recovery
    /// property this whole feature depends on.
    /// </summary>
    public static KeyPair FromSeed(byte[] seed)
    {
        if (seed.Length < 32)
            throw new ArgumentException("Seed must be at least 32 bytes.", nameof(seed));

        var raw = new BigInteger(seed[..32], isUnsigned: true, isBigEndian: true);
        var scalar = (raw % (EcMath.Order - 1)) + 1; // land in [1, n-1]

        var point = EcMath.ScalarMultiplyBasePoint(scalar);

        var ecParams = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = EcMath.ToFixedBytes(scalar, 32),
            Q = new ECPoint
            {
                X = EcMath.ToFixedBytes(point.X, 32),
                Y = EcMath.ToFixedBytes(point.Y, 32)
            }
        };

        using var ecdsa = ECDsa.Create(ecParams);
        var priv = ecdsa.ExportPkcs8PrivateKey();
        var pub = ecdsa.ExportSubjectPublicKeyInfo();
        return new KeyPair(priv, pub);
    }
}
