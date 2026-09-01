using System;
using System.IO;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class UnlockThrottleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _walletPath;

    public UnlockThrottleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KeyWalletThrottleTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _walletPath = Path.Combine(_dir, "wallet.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Load_NoSidecar_ReturnsZeroFailuresAndNoWait()
    {
        var throttle = UnlockThrottle.Load(_walletPath);

        Assert.Equal(0, throttle.FailedAttempts);
        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingWait());
    }

    [Fact]
    public void GetRemainingWait_WithinFreeAttempts_IsZero()
    {
        var throttle = new UnlockThrottle { FailedAttempts = 2, LastFailureUtc = DateTime.UtcNow };
        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingWait());
    }

    [Fact]
    public void GetRemainingWait_PastFreeAttempts_IsPositive()
    {
        var throttle = new UnlockThrottle { FailedAttempts = 3, LastFailureUtc = DateTime.UtcNow };
        Assert.True(throttle.GetRemainingWait() > TimeSpan.Zero);
    }

    [Fact]
    public void GetRemainingWait_DelayGrowsWithMoreFailures()
    {
        var now = DateTime.UtcNow;
        var fewer = new UnlockThrottle { FailedAttempts = 3, LastFailureUtc = now };
        var more = new UnlockThrottle { FailedAttempts = 5, LastFailureUtc = now };

        Assert.True(more.GetRemainingWait() > fewer.GetRemainingWait());
    }

    [Fact]
    public void GetRemainingWait_IsCapped_EvenForManyFailures()
    {
        var throttle = new UnlockThrottle { FailedAttempts = 30, LastFailureUtc = DateTime.UtcNow };

        // Capped at 5 minutes -- without a cap, 1 << 28 seconds would be an
        // absurd multi-year wait, so this also guards against that.
        Assert.True(throttle.GetRemainingWait() <= TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void GetRemainingWait_ElapsesOverTime()
    {
        var throttle = new UnlockThrottle
        {
            FailedAttempts = 3,
            LastFailureUtc = DateTime.UtcNow.AddSeconds(-100) // long past the short delay for attempt 3
        };

        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingWait());
    }

    [Fact]
    public void RecordFailure_PersistsAcrossLoad()
    {
        var throttle = UnlockThrottle.Load(_walletPath);
        throttle.RecordFailure(_walletPath);
        throttle.RecordFailure(_walletPath);
        throttle.RecordFailure(_walletPath);

        var reloaded = UnlockThrottle.Load(_walletPath);

        Assert.Equal(3, reloaded.FailedAttempts);
        Assert.True(reloaded.GetRemainingWait() > TimeSpan.Zero);
    }

    [Fact]
    public void Reset_ClearsPersistedState()
    {
        var throttle = UnlockThrottle.Load(_walletPath);
        throttle.RecordFailure(_walletPath);
        throttle.RecordFailure(_walletPath);
        throttle.RecordFailure(_walletPath);

        UnlockThrottle.Reset(_walletPath);

        var reloaded = UnlockThrottle.Load(_walletPath);
        Assert.Equal(0, reloaded.FailedAttempts);
        Assert.Equal(TimeSpan.Zero, reloaded.GetRemainingWait());
    }

    [Fact]
    public void Load_CorruptedSidecar_FailsOpenToZeroFailures()
    {
        File.WriteAllText(_walletPath + ".lockout", "{ not valid json");

        var throttle = UnlockThrottle.Load(_walletPath);

        Assert.Equal(0, throttle.FailedAttempts);
    }
}
