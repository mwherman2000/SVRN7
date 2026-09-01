using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class WalletFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public WalletFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KeyWalletTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "wallet.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static char[] Pwd(string s) => s.ToCharArray();

    [Fact]
    public void CreateSaveLoadUnlockSign_RoundTrips()
    {
        // End-to-end regression for the original bug report: create, save,
        // reload from disk, unlock, then sign -- all four steps must work
        // together, not just in isolation.
        using var keyPair = KeyPair.Generate();
        var walletFile = WalletFile.Create(keyPair, Pwd("test-password-123"));
        walletFile.Save(_path);

        var loaded = WalletFile.Load(_path);
        using var unlocked = loaded.Unlock(Pwd("test-password-123"));

        var message = Encoding.UTF8.GetBytes("hello");
        var signature = unlocked.Sign(message);
        Assert.True(KeyPair.Verify(unlocked.PublicKeySubjectPublicKeyInfo, message, signature));
    }

    [Fact]
    public void Load_CorruptedJson_ThrowsWithClearMessage()
    {
        File.WriteAllText(_path, "{ not valid json");

        var ex = Assert.Throws<InvalidDataException>(() => WalletFile.Load(_path));
        Assert.Contains(_path, ex.Message);
    }

    [Fact]
    public void Load_CorruptedJsonWithBackupPresent_MentionsBackupInMessage()
    {
        using var keyPair = KeyPair.Generate();
        var walletFile = WalletFile.Create(keyPair, Pwd("test-password-123"));
        walletFile.Save(_path); // first save: no prior file, no .bak yet
        walletFile.Save(_path); // second save: path now exists, creates .bak

        File.WriteAllText(_path, "{ not valid json");

        var ex = Assert.Throws<InvalidDataException>(() => WalletFile.Load(_path));
        Assert.Contains(".bak", ex.Message);
    }

    [Fact]
    public void Unlock_OldPbkdf2FormatWallet_StillWorks()
    {
        // Hand-build a Version 1 (PBKDF2) wallet file the way pre-Argon2id
        // code produced it, and confirm it still unlocks unchanged -- new
        // wallets must not break old ones.
        using var keyPair = KeyPair.Generate();
        var encrypted = WalletCrypto.EncryptV1(keyPair.PrivateKeyPkcs8, Pwd("legacy-password-123"));
        var legacyJson = JsonSerializer.Serialize(new
        {
            Version = 1,
            PublicKeyBase64 = keyPair.PublicKeyBase64,
            EncryptedPrivateKeyBase64 = Convert.ToBase64String(encrypted),
            CreatedUtc = DateTime.UtcNow
        });
        File.WriteAllText(_path, legacyJson);

        var loaded = WalletFile.Load(_path);
        using var unlocked = loaded.Unlock(Pwd("legacy-password-123"));

        Assert.Equal(keyPair.PublicKeyBase64, unlocked.PublicKeyBase64);
    }

    [Fact]
    public void Create_AlwaysWritesCurrentVersion()
    {
        using var keyPair = KeyPair.Generate();
        var walletFile = WalletFile.Create(keyPair, Pwd("test-password-123"));

        Assert.Equal(2, walletFile.Version);
    }
}
