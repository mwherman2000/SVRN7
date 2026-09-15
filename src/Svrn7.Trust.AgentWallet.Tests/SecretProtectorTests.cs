using System.Security.Cryptography;
using System.Text;
using Svrn7.Trust.AgentWallet;

// DpapiSecretProtector calls are guarded by OperatingSystem.IsWindows(); the
// analyzer can't see that through Assert.Throws lambdas.
#pragma warning disable CA1416

namespace AgentWallet.Tests;

public class SecretProtectorTests
{
    [Fact]
    public void NullSecretProtector_IsDisabled_AndThrowsIfUsed()
    {
        var p = new NullSecretProtector();

        Assert.False(p.Enabled);
        Assert.Throws<PlatformNotSupportedException>(() => p.Protect(new byte[] { 1 }));
        Assert.Throws<PlatformNotSupportedException>(() => p.Unprotect(new byte[] { 1 }));
    }

    [Fact]
    public void CreateDefault_MatchesPlatform()
    {
        var p = SecretProtectors.CreateDefault();
        Assert.Equal(OperatingSystem.IsWindows(), p.Enabled);
    }

    [Fact]
    public void Dpapi_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;

        var p = new DpapiSecretProtector();
        var secret = Encoding.UTF8.GetBytes("wallet-password-material");

        var sealedBlob = p.Protect(secret);
        Assert.NotEqual(secret, sealedBlob);
        Assert.Equal(secret, p.Unprotect(sealedBlob));
    }

    [Fact]
    public void Dpapi_TamperedBlob_Throws()
    {
        if (!OperatingSystem.IsWindows()) return;

        var p = new DpapiSecretProtector();
        var sealedBlob = p.Protect(Encoding.UTF8.GetBytes("x"));
        sealedBlob[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => p.Unprotect(sealedBlob));
    }
}
