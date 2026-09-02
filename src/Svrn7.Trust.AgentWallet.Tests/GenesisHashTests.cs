using Svrn7.Trust.AgentWallet;

namespace AgentWallet.Tests;

public class GenesisHashTests
{
    [Fact]
    public void Compute_IsDeterministic_And64Hex()
    {
        using var k = RecoveryPhrase.Derive(RecoveryPhrase.Generate());

        var h1 = GenesisHash.Compute(k.Secp256k1PublicKeyHex);
        var h2 = GenesisHash.Compute(k.Secp256k1PublicKeyHex);

        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length);
        Assert.Matches("^[0-9a-f]{64}$", h1);
    }

    [Fact]
    public void Compute_ByteAndHexOverloads_Agree()
    {
        using var k = RecoveryPhrase.Derive(RecoveryPhrase.Generate());
        var bytes = Convert.FromHexString(k.Secp256k1PublicKeyHex);

        Assert.Equal(GenesisHash.Compute(bytes), GenesisHash.Compute(k.Secp256k1PublicKeyHex));
    }

    [Fact]
    public void Compute_DiffersPerKey()
    {
        using var a = RecoveryPhrase.Derive(RecoveryPhrase.Generate());
        using var b = RecoveryPhrase.Derive(RecoveryPhrase.Generate());

        Assert.NotEqual(GenesisHash.Compute(a.Secp256k1PublicKeyHex), GenesisHash.Compute(b.Secp256k1PublicKeyHex));
    }

    [Fact]
    public void Slug_IsPrefixOfHash()
    {
        using var k = RecoveryPhrase.Derive(RecoveryPhrase.Generate());

        var full = GenesisHash.Compute(k.Secp256k1PublicKeyHex);
        Assert.Equal(full[..8], GenesisHash.Slug(k.Secp256k1PublicKeyHex));
        Assert.Equal(full[..16], GenesisHash.Slug(k.Secp256k1PublicKeyHex, 16));
    }
}
