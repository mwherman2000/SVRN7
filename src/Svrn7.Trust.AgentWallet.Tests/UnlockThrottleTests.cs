using Svrn7.Trust.AgentWallet;

namespace AgentWallet.Tests;

public class UnlockThrottleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _walletPath;

    public UnlockThrottleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AgentWalletThrottleTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _walletPath = Path.Combine(_dir, "agent-identity.wallet");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void NoSidecar_NoWait()
    {
        Assert.Equal(TimeSpan.Zero, UnlockThrottle.Load(_walletPath).GetRemainingWait());
    }

    [Fact]
    public void FirstTwoFailures_AreFree()
    {
        var t = new UnlockThrottle();
        t.RecordFailure(_walletPath);
        t.RecordFailure(_walletPath);

        Assert.Equal(TimeSpan.Zero, UnlockThrottle.Load(_walletPath).GetRemainingWait());
    }

    [Fact]
    public void ThirdFailure_ImposesWait_ThatGrows()
    {
        var t = new UnlockThrottle();
        for (var i = 0; i < 3; i++) t.RecordFailure(_walletPath);
        var afterThree = UnlockThrottle.Load(_walletPath).GetRemainingWait();

        t.RecordFailure(_walletPath);
        var afterFour = UnlockThrottle.Load(_walletPath).GetRemainingWait();

        Assert.True(afterThree > TimeSpan.Zero);
        Assert.True(afterFour > afterThree);
    }

    [Fact]
    public void Reset_ClearsSidecar()
    {
        var t = new UnlockThrottle();
        for (var i = 0; i < 5; i++) t.RecordFailure(_walletPath);
        Assert.True(File.Exists(_walletPath + ".lockout"));

        UnlockThrottle.Reset(_walletPath);

        Assert.False(File.Exists(_walletPath + ".lockout"));
        Assert.Equal(TimeSpan.Zero, UnlockThrottle.Load(_walletPath).GetRemainingWait());
    }

    [Fact]
    public void CorruptSidecar_FailsOpen()
    {
        File.WriteAllText(_walletPath + ".lockout", "{ this is not valid json");

        var t = UnlockThrottle.Load(_walletPath);

        Assert.Equal(0, t.FailedAttempts);
        Assert.Equal(TimeSpan.Zero, t.GetRemainingWait());
    }
}
