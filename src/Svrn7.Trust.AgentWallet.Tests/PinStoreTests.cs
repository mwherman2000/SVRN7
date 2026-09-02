using Svrn7.Trust.AgentWallet;

#pragma warning disable CA1416 // DpapiPinStore calls are guarded by OperatingSystem.IsWindows()

namespace AgentWallet.Tests;

public class PinStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PinStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AgentWalletPinTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "pin-store.bin");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void WalletPin_Compute_IsDeterministicAndKeySpecific()
    {
        using var a = RecoveryPhrase.Derive(RecoveryPhrase.Generate());
        using var b = RecoveryPhrase.Derive(RecoveryPhrase.Generate());

        Assert.Equal(WalletPin.Compute(a.Secp256k1PublicKeyHex), WalletPin.Compute(a.Secp256k1PublicKeyHex));
        Assert.NotEqual(WalletPin.Compute(a.Secp256k1PublicKeyHex), WalletPin.Compute(b.Secp256k1PublicKeyHex));
        Assert.Equal(32, WalletPin.Compute(a.Secp256k1PublicKeyHex).Length); // SHA-256
        Assert.Equal(WalletPin.Compute(a.Secp256k1PublicKeyHex),
                     WalletPin.Compute(Convert.FromHexString(a.Secp256k1PublicKeyHex)));
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
    public void InMemoryPinStore_RoundTrips_AndCopiesIn()
    {
        var store = new InMemoryPinStore();
        var pin = new byte[] { 9, 8, 7 };

        Assert.True(store.Enabled);
        store.Set("w1", pin);
        pin[0] = 42; // mutate after store
        Assert.Equal(new byte[] { 9, 8, 7 }, store.TryGet("w1"));
        Assert.Null(store.TryGet("w2"));

        store.Remove("w1");
        Assert.Null(store.TryGet("w1"));
    }

    [Fact]
    public void CreateDefault_MatchesPlatform()
    {
        var result = PinStores.CreateDefault();
        Assert.Equal(OperatingSystem.IsWindows(), result.Store.Enabled);
    }

    [Fact]
    public void DpapiPinStore_PersistsAcrossInstances()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pin = new byte[] { 1, 2, 3, 4, 5 };
        new DpapiPinStore(_path).Set("wallet-a", pin);

        var reader = new DpapiPinStore(_path);
        Assert.Equal(pin, reader.TryGet("wallet-a"));
        Assert.Null(reader.TryGet("wallet-b"));
    }

    [Fact]
    public void DpapiPinStore_TamperedFile_ThrowsInvalidData()
    {
        if (!OperatingSystem.IsWindows()) return;

        new DpapiPinStore(_path).Set("w", new byte[] { 1, 2, 3 });
        var bytes = File.ReadAllBytes(_path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(_path, bytes);

        var ex = Assert.Throws<InvalidDataException>(() => new DpapiPinStore(_path));
        Assert.Contains(_path, ex.Message);
    }
}
