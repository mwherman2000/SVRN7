using System.Security.Cryptography;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class WalletCryptoTests
{
    private static char[] Pwd(string s) => s.ToCharArray();

    [Fact]
    public void V2_EncryptDecrypt_RoundTrips()
    {
        var plaintext = new byte[] { 1, 2, 3, 4, 5 };
        var encrypted = WalletCrypto.EncryptV2(plaintext, Pwd("correct horse battery staple"));

        var decrypted = WalletCrypto.DecryptV2(encrypted, Pwd("correct horse battery staple"));

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void V2_WrongPassword_Throws()
    {
        var plaintext = new byte[] { 1, 2, 3 };
        var encrypted = WalletCrypto.EncryptV2(plaintext, Pwd("right-password-123"));

        // AesGcm throws the more specific AuthenticationTagMismatchException
        // (a CryptographicException subclass) -- ThrowsAny matches by base
        // type, same as Program.cs's catch (CryptographicException).
        Assert.ThrowsAny<CryptographicException>(() => WalletCrypto.DecryptV2(encrypted, Pwd("wrong-password-456")));
    }

    [Fact]
    public void V2_TamperedCiphertext_Throws()
    {
        var plaintext = new byte[] { 1, 2, 3 };
        var encrypted = WalletCrypto.EncryptV2(plaintext, Pwd("some-password-123"));
        encrypted[^1] ^= 0xFF; // flip a bit in the ciphertext tail

        Assert.ThrowsAny<CryptographicException>(() => WalletCrypto.DecryptV2(encrypted, Pwd("some-password-123")));
    }

    [Fact]
    public void V1_EncryptDecrypt_RoundTrips_ForBackwardCompatibility()
    {
        // Version 1 (PBKDF2) must stay byte-for-byte compatible so wallets
        // created before the Argon2id upgrade keep unlocking.
        var plaintext = new byte[] { 9, 8, 7 };
        var encrypted = WalletCrypto.EncryptV1(plaintext, Pwd("legacy-password-123"));

        var decrypted = WalletCrypto.DecryptV1(encrypted, Pwd("legacy-password-123"));

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void V1_WrongPassword_Throws()
    {
        var plaintext = new byte[] { 1, 2, 3 };
        var encrypted = WalletCrypto.EncryptV1(plaintext, Pwd("right-password-123"));

        Assert.ThrowsAny<CryptographicException>(() => WalletCrypto.DecryptV1(encrypted, Pwd("wrong-password-456")));
    }
}
