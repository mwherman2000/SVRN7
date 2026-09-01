using System.Text;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class KeyPairSessionTests
{
    private static byte[] TrySign(KeyPair keyPair) => keyPair.Sign(Encoding.UTF8.GetBytes("probe"));

    [Fact]
    public void Replace_DisposesThePreviousKey()
    {
        var session = new KeyPairSession();
        var first = KeyPair.Generate();
        session.Replace(first);

        var second = KeyPair.Generate();
        session.Replace(second);

        Assert.Throws<ObjectDisposedException>(() => TrySign(first));
        Assert.NotEmpty(TrySign(second)); // current key still live
        session.Dispose();
    }

    [Fact]
    public void Replace_WithSameInstance_DoesNotDisposeIt()
    {
        using var session = new KeyPairSession();
        var key = KeyPair.Generate();
        session.Replace(key);

        session.Replace(key); // e.g. a re-encrypt handing back the still-live key

        Assert.NotEmpty(TrySign(key));
        Assert.Same(key, session.Current);
    }

    [Fact]
    public void Lock_DisposesAndClearsCurrent()
    {
        using var session = new KeyPairSession();
        var key = KeyPair.Generate();
        session.Replace(key);

        session.Lock();

        Assert.False(session.IsUnlocked);
        Assert.Null(session.Current);
        Assert.Throws<ObjectDisposedException>(() => TrySign(key));
    }

    [Fact]
    public void IsUnlocked_TracksCurrentKey()
    {
        using var session = new KeyPairSession();
        Assert.False(session.IsUnlocked);

        session.Replace(KeyPair.Generate());
        Assert.True(session.IsUnlocked);

        session.Replace(null);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void Dispose_DisposesTheHeldKey()
    {
        var session = new KeyPairSession();
        var key = KeyPair.Generate();
        session.Replace(key);

        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => TrySign(key));
    }
}
