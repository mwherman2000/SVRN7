using System;
using System.Security.Cryptography;
using System.Text;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class KeyPairTests
{
    [Fact]
    public void GenerateSignVerify_RoundTrips()
    {
        var keyPair = KeyPair.Generate();
        var message = Encoding.UTF8.GetBytes("hello");

        var signature = keyPair.Sign(message);

        Assert.True(KeyPair.Verify(keyPair.PublicKeySubjectPublicKeyInfo, message, signature));
    }

    [Fact]
    public void FromPrivateKey_DoesNotAliasCallersBuffer()
    {
        // Regression test for the original bug: KeyPair.FromPrivateKey used
        // to store the caller's array by reference. WalletFile.Unlock zeroes
        // its own buffer immediately after calling FromPrivateKey, which
        // used to zero the returned KeyPair's key out from under it too --
        // "Unlocked." would print, then Sign() would fail as if the
        // password were wrong.
        var original = KeyPair.Generate();
        var callerOwnedCopy = (byte[])original.PrivateKeyPkcs8.Clone();

        var keyPair = KeyPair.FromPrivateKey(callerOwnedCopy);
        Array.Clear(callerOwnedCopy);

        var message = Encoding.UTF8.GetBytes("hello");
        var signature = keyPair.Sign(message);
        Assert.True(KeyPair.Verify(keyPair.PublicKeySubjectPublicKeyInfo, message, signature));
    }

    [Fact]
    public void Dispose_ZeroesKeyAndSignThrows()
    {
        var keyPair = KeyPair.Generate();
        keyPair.Dispose();

        Assert.All(keyPair.PrivateKeyPkcs8, b => Assert.Equal((byte)0, b));
        Assert.Throws<ObjectDisposedException>(() => keyPair.Sign(Encoding.UTF8.GetBytes("x")));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var keyPair = KeyPair.Generate();
        keyPair.Dispose();
        keyPair.Dispose(); // must not throw
    }

    [Fact]
    public void FromSeed_IsDeterministic()
    {
        var seed = new byte[64];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)i;

        var keyPair1 = KeyPair.FromSeed((byte[])seed.Clone());
        var keyPair2 = KeyPair.FromSeed((byte[])seed.Clone());

        Assert.Equal(keyPair1.PublicKeyBase64, keyPair2.PublicKeyBase64);
    }
}
