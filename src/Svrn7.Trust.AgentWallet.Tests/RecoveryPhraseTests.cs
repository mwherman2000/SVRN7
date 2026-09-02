using Svrn7.Trust.AgentWallet;

namespace AgentWallet.Tests;

public class RecoveryPhraseTests
{
    [Fact]
    public void Generate_Produces12ValidWords()
    {
        var phrase = RecoveryPhrase.Generate();

        Assert.Equal(12, phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        RecoveryPhrase.Validate(phrase); // does not throw
    }

    [Fact]
    public void Generate_IsRandom()
    {
        Assert.NotEqual(RecoveryPhrase.Generate(), RecoveryPhrase.Generate());
    }

    [Theory]
    [InlineData("word word word")]                                              // too few
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")] // 13
    [InlineData("zzzzz abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")]          // unknown word
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")]        // bad checksum
    public void Validate_RejectsBadPhrases(string phrase)
    {
        Assert.Throws<FormatException>(() => RecoveryPhrase.Validate(phrase));
    }

    [Fact]
    public void Derive_IsDeterministic_ForSamePhrase()
    {
        var phrase = RecoveryPhrase.Generate();

        using var a = RecoveryPhrase.Derive(phrase);
        using var b = RecoveryPhrase.Derive(phrase);

        Assert.Equal(a.Secp256k1PublicKeyHex, b.Secp256k1PublicKeyHex);
        Assert.Equal(a.X25519PublicKeyHex, b.X25519PublicKeyHex);
        Assert.Equal(Convert.ToHexString(a.Secp256k1PrivateKey), Convert.ToHexString(b.Secp256k1PrivateKey));
        Assert.Equal(Convert.ToHexString(a.X25519PrivateKey), Convert.ToHexString(b.X25519PrivateKey));
    }

    [Fact]
    public void Derive_DifferentPhrases_DifferentKeys()
    {
        using var a = RecoveryPhrase.Derive(RecoveryPhrase.Generate());
        using var b = RecoveryPhrase.Derive(RecoveryPhrase.Generate());

        Assert.NotEqual(a.Secp256k1PublicKeyHex, b.Secp256k1PublicKeyHex);
        Assert.NotEqual(a.X25519PublicKeyHex, b.X25519PublicKeyHex);
    }

    [Fact]
    public void Derive_KeyShapes()
    {
        using var k = RecoveryPhrase.Derive(RecoveryPhrase.Generate());

        Assert.Equal(32, k.Secp256k1PrivateKey.Length);
        Assert.Equal(66, k.Secp256k1PublicKeyHex.Length);          // 33 bytes compressed
        Assert.True(k.Secp256k1PublicKeyHex.StartsWith("02") || k.Secp256k1PublicKeyHex.StartsWith("03"));
        Assert.Equal(32, k.X25519PrivateKey.Length);
        Assert.Equal(64, k.X25519PublicKeyHex.Length);             // 32 bytes
    }

    [Fact]
    public void Derive_X25519PrivateKey_IsClamped()
    {
        using var k = RecoveryPhrase.Derive(RecoveryPhrase.Generate());

        Assert.Equal(0, k.X25519PrivateKey[0] & 0x07);   // low 3 bits clear
        Assert.Equal(0, k.X25519PrivateKey[31] & 0x80);  // top bit clear
        Assert.Equal(0x40, k.X25519PrivateKey[31] & 0x40); // bit 6 set
    }

    [Fact]
    public void Dispose_ZeroesPrivateKeys()
    {
        var k = RecoveryPhrase.Derive(RecoveryPhrase.Generate());
        k.Dispose();

        Assert.Equal(new byte[32], k.Secp256k1PrivateKey);
        Assert.Equal(new byte[32], k.X25519PrivateKey);
    }

    [Fact]
    public void Derive_Passphrase_ChangesKeys()
    {
        var phrase = RecoveryPhrase.Generate();

        using var plain = RecoveryPhrase.Derive(phrase);
        using var withPass = RecoveryPhrase.Derive(phrase, "extra");

        Assert.NotEqual(plain.Secp256k1PublicKeyHex, withPass.Secp256k1PublicKeyHex);
    }
}
