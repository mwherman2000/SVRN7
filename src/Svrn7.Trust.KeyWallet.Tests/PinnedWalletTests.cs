using System;
using System.IO;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class PinnedWalletTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private const string WalletId = "test-wallet";

    public PinnedWalletTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KeyWalletPinnedTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "wallet.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static char[] Pwd(string s) => s.ToCharArray();

    private void WriteWallet(KeyPair keyPair) =>
        WalletFile.Create(keyPair, Pwd("pinned-wallet-pw-123")).Save(_path);

    [Fact]
    public void Load_NothingPinned_IsFirstUse()
    {
        using var keyPair = KeyPair.Generate();
        WriteWallet(keyPair);

        var result = PinnedWallet.Load(_path, WalletId, new InMemoryPinStore());

        Assert.Equal(PinCheck.FirstUse, result.Check);
        Assert.Equal(WalletPin.Compute(keyPair.PublicKeyBase64), result.ActualPin);
    }

    [Fact]
    public void Load_PinMatchesFile_IsMatch()
    {
        using var keyPair = KeyPair.Generate();
        WriteWallet(keyPair);

        var store = new InMemoryPinStore();
        store.Set(WalletId, WalletPin.Compute(keyPair.PublicKeyBase64));

        Assert.Equal(PinCheck.Match, PinnedWallet.Load(_path, WalletId, store).Check);
    }

    [Fact]
    public void Load_FileSwappedForDifferentKey_IsMismatch()
    {
        using (var original = KeyPair.Generate())
            WriteWallet(original);

        var store = new InMemoryPinStore();
        using (var original = KeyPair.Generate())
            store.Set(WalletId, WalletPin.Compute(original.PublicKeyBase64));

        // The pin above is for a key that is not the one on disk -- i.e. the
        // wallet file was replaced with an attacker's own wallet.
        Assert.Equal(PinCheck.Mismatch, PinnedWallet.Load(_path, WalletId, store).Check);
    }

    [Fact]
    public void Load_DisabledStore_IsAlwaysFirstUse()
    {
        using var keyPair = KeyPair.Generate();
        WriteWallet(keyPair);

        Assert.Equal(PinCheck.FirstUse, PinnedWallet.Load(_path, WalletId, new NullPinStore()).Check);
    }

    [Fact]
    public void Load_ReturnsUsableWallet()
    {
        using var keyPair = KeyPair.Generate();
        WriteWallet(keyPair);

        var result = PinnedWallet.Load(_path, WalletId, new InMemoryPinStore());
        using var unlocked = result.Wallet.Unlock(Pwd("pinned-wallet-pw-123"));

        Assert.Equal(keyPair.PublicKeyBase64, unlocked.PublicKeyBase64);
    }
}
