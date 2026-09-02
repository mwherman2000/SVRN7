using System.Security.Cryptography;
using System.Text;
using Svrn7.Trust.AgentWallet;

namespace AgentWallet.Tests;

public class WalletCryptoTests
{
    private static char[] Pw(string s) => s.ToCharArray();

    [Fact]
    public void EncryptV2_DecryptV2_RoundTrips()
    {
        var plaintext = Encoding.UTF8.GetBytes("""{"did":"did:drn:test","k":"abc"}""");

        var blob = WalletCrypto.EncryptV2(plaintext, Pw("correct horse"));
        var back = WalletCrypto.DecryptV2(blob, Pw("correct horse"));

        Assert.Equal(plaintext, back);
    }

    [Fact]
    public void DecryptV2_WrongPassword_Throws()
    {
        var blob = WalletCrypto.EncryptV2(Encoding.UTF8.GetBytes("secret"), Pw("right"));

        Assert.ThrowsAny<CryptographicException>(() => WalletCrypto.DecryptV2(blob, Pw("wrong")));
    }

    [Fact]
    public void DecryptV2_TamperedCiphertext_Throws()
    {
        var blob = WalletCrypto.EncryptV2(Encoding.UTF8.GetBytes("secret payload"), Pw("pw"));
        blob[^1] ^= 0xFF; // flip a bit in the ciphertext

        Assert.ThrowsAny<CryptographicException>(() => WalletCrypto.DecryptV2(blob, Pw("pw")));
    }

    [Fact]
    public void EncryptV2_EmbedsArgon2CostHeader()
    {
        var blob = WalletCrypto.EncryptV2(Encoding.UTF8.GetBytes("x"), Pw("pw"));

        // header = memoryKiB(4) ‖ iterations(4) ‖ parallelism(4)
        Assert.Equal(65536, BitConverter.ToInt32(blob, 0));
        Assert.Equal(3, BitConverter.ToInt32(blob, 4));
        Assert.Equal(4, BitConverter.ToInt32(blob, 8));
    }

    [Fact]
    public void EncryptV2_TwoCallsSamePlaintext_DifferBySaltAndNonce()
    {
        var pt = Encoding.UTF8.GetBytes("same");
        var a = WalletCrypto.EncryptV2(pt, Pw("pw"));
        var b = WalletCrypto.EncryptV2(pt, Pw("pw"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeriveDefaultKey_SameSalt_SameKey()
    {
        var (key1, salt) = WalletCrypto.DeriveDefaultKey(Pw("pw"));
        var key2 = WalletCrypto.DeriveDefaultKey(Pw("pw"), salt);

        Assert.Equal(key1, key2);
        Assert.Equal(32, key1.Length);
    }

    [Fact]
    public void EncryptWithKey_DecryptWithKey_RoundTrips_AndZeroesKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var keyCopy = (byte[])key.Clone();
        var salt = WalletCrypto.NewSalt();
        var payload = RandomNumberGenerator.GetBytes(32);

        var blob = WalletCrypto.EncryptWithKey(payload, key, prefix: salt);
        Assert.Equal(new byte[32], key); // EncryptWithKey zeroes the key it was given

        var rest = blob[salt.Length..];
        var back = WalletCrypto.DecryptWithKey(rest, keyCopy);
        Assert.Equal(payload, back);
    }
}
