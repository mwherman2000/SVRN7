using System;
using System.IO;
using Svrn7.Trust.KeyWallet;
using Xunit;

// Every DpapiPinStore call below is guarded by `if (!OperatingSystem.IsWindows()) return;`
// at the top of its test. The analyzer can't see that through a lambda
// (Assert.Throws), so silence the platform-compat warning for this file.
#pragma warning disable CA1416

namespace KeyWallet.Tests;

public class PinStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PinStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KeyWalletPinTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "pin-store.bin");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void WalletPin_Compute_IsDeterministicAndKeySpecific()
    {
        using var a = KeyPair.Generate();
        using var b = KeyPair.Generate();

        Assert.Equal(WalletPin.Compute(a.PublicKeyBase64), WalletPin.Compute(a.PublicKeyBase64));
        Assert.NotEqual(WalletPin.Compute(a.PublicKeyBase64), WalletPin.Compute(b.PublicKeyBase64));
        Assert.Equal(32, WalletPin.Compute(a.PublicKeyBase64).Length); // SHA-256
    }

    [Fact]
    public void NullPinStore_PinsNothing()
    {
        var store = new NullPinStore();

        Assert.False(store.Enabled);
        store.Set("w", new byte[] { 1, 2, 3 });
        Assert.Null(store.TryGet("w"));
    }

    [Fact]
    public void InMemoryPinStore_RoundTrips()
    {
        var store = new InMemoryPinStore();
        var pin = new byte[] { 9, 8, 7, 6 };

        Assert.True(store.Enabled);
        Assert.Null(store.TryGet("w1"));

        store.Set("w1", pin);
        Assert.Equal(pin, store.TryGet("w1"));
        Assert.Null(store.TryGet("w2"));

        store.Remove("w1");
        Assert.Null(store.TryGet("w1"));
    }

    [Fact]
    public void InMemoryPinStore_CopiesIn_NoAliasing()
    {
        var store = new InMemoryPinStore();
        var pin = new byte[] { 1, 2, 3, 4 };
        store.Set("w", pin);

        pin[0] = 99; // mutate the caller's array after storing

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, store.TryGet("w"));
    }

    [Fact]
    public void DpapiPinStore_PersistsAcrossInstances()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only

        using var keyPair = KeyPair.Generate();
        var pin = WalletPin.Compute(keyPair.PublicKeyBase64);

        var writer = new DpapiPinStore(_path);
        writer.Set("wallet-a", pin);

        var reader = new DpapiPinStore(_path);
        Assert.Equal(pin, reader.TryGet("wallet-a"));
        Assert.Null(reader.TryGet("wallet-b"));
    }

    [Fact]
    public void DpapiPinStore_MissingFile_IsEmptyNotError()
    {
        if (!OperatingSystem.IsWindows()) return;

        var store = new DpapiPinStore(_path);
        Assert.True(store.Enabled);
        Assert.Null(store.TryGet("anything"));
    }

    [Fact]
    public void DpapiPinStore_TamperedFile_ThrowsInvalidData()
    {
        if (!OperatingSystem.IsWindows()) return;

        new DpapiPinStore(_path).Set("w", new byte[] { 1, 2, 3 });

        var bytes = File.ReadAllBytes(_path);
        bytes[^1] ^= 0xFF; // flip a bit in the DPAPI blob
        File.WriteAllBytes(_path, bytes);

        var ex = Assert.Throws<InvalidDataException>(() => new DpapiPinStore(_path));
        Assert.Contains(_path, ex.Message);
    }

    [Fact]
    public void DpapiPinStore_Remove_Persists()
    {
        if (!OperatingSystem.IsWindows()) return;

        var store = new DpapiPinStore(_path);
        store.Set("w", new byte[] { 4, 5, 6 });
        store.Remove("w");

        Assert.Null(new DpapiPinStore(_path).TryGet("w"));
    }
}
