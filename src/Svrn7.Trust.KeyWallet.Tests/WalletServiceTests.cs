using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class WalletServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly InMemoryPinStore _pins = new();

    public WalletServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KeyWalletServiceTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "wallet.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static char[] Pwd(string s) => s.ToCharArray();

    private WalletService NewService(IPinStore? store = null) => new(_path, store ?? _pins);

    // --- Create ----------------------------------------------------------------

    [Fact]
    public void Create_WritesEncryptedWallet_AndPinsPublicKey()
    {
        var service = NewService();

        var result = service.Create(Pwd("create-pw-123"));
        try
        {
            Assert.True(File.Exists(_path));
            Assert.True(result.PublicKeyPinned);
            Assert.Equal(WalletPin.Compute(result.PublicKeyBase64), _pins.TryGet(Path.GetFullPath(_path)));

            // The private key never lands in the file as plaintext.
            var raw = File.ReadAllText(_path);
            Assert.DoesNotContain(Convert.ToBase64String(result.KeyPair.PrivateKeyPkcs8), raw);
        }
        finally
        {
            result.KeyPair.Dispose();
        }
    }

    [Fact]
    public void Create_ResetsUnlockThrottle()
    {
        WriteLockout(failedAttempts: 5);

        NewService().Create(Pwd("create-pw-123")).KeyPair.Dispose();

        Assert.Equal(0, UnlockThrottle.Load(_path).FailedAttempts);
    }

    [Fact]
    public void Create_WithDisabledStore_ReportsNotPinned()
    {
        var result = NewService(new NullPinStore()).Create(Pwd("create-pw-123"));
        try
        {
            Assert.False(result.PublicKeyPinned);
        }
        finally
        {
            result.KeyPair.Dispose();
        }
    }

    // --- Unlock --------------------------------------------------------------

    [Fact]
    public void Unlock_NoWalletFile_ReturnsNoWalletFile()
    {
        var result = NewService().Unlock(() => throw new Xunit.Sdk.XunitException("password should not be requested"));

        var noFile = Assert.IsType<UnlockResult.NoWalletFile>(result);
        Assert.Equal(_path, noFile.Path);
    }

    [Fact]
    public void Unlock_CorrectPassword_ReturnsSuccess_AndEnrollsPinOnFirstUse()
    {
        // Wallet written straight through WalletFile so nothing is pinned yet.
        using (var kp = KeyPair.Generate())
            WalletFile.Create(kp, Pwd("unlock-pw-123")).Save(_path);

        var service = NewService();
        var result = service.Unlock(() => Pwd("unlock-pw-123"));

        var success = Assert.IsType<UnlockResult.Success>(result);
        try
        {
            Assert.True(success.PinnedOnFirstUse);
            Assert.Equal(
                WalletPin.Compute(success.KeyPair.PublicKeyBase64),
                _pins.TryGet(Path.GetFullPath(_path)));
        }
        finally
        {
            success.KeyPair.Dispose();
        }
    }

    [Fact]
    public void Unlock_SecondTime_DoesNotReportFirstUsePinning()
    {
        NewService().Create(Pwd("unlock-pw-123")).KeyPair.Dispose(); // pins on write

        var result = NewService().Unlock(() => Pwd("unlock-pw-123"));

        var success = Assert.IsType<UnlockResult.Success>(result);
        success.KeyPair.Dispose();
        Assert.False(success.PinnedOnFirstUse);
    }

    [Fact]
    public void Unlock_WrongPassword_ReturnsWrongPassword_AndRecordsThrottleFailure()
    {
        NewService().Create(Pwd("right-pw-123")).KeyPair.Dispose();

        var result = NewService().Unlock(() => Pwd("wrong-pw-123"));

        Assert.IsType<UnlockResult.WrongPassword>(result);
        Assert.Equal(1, UnlockThrottle.Load(_path).FailedAttempts);
    }

    [Fact]
    public void Unlock_SuccessAfterFailures_ClearsThrottle()
    {
        NewService().Create(Pwd("right-pw-123")).KeyPair.Dispose();
        NewService().Unlock(() => Pwd("wrong-pw-123"));
        Assert.Equal(1, UnlockThrottle.Load(_path).FailedAttempts);

        var recovered = NewService().Unlock(() => Pwd("right-pw-123"));
        Assert.IsType<UnlockResult.Success>(recovered);
        ((UnlockResult.Success)recovered).KeyPair.Dispose();

        Assert.Equal(0, UnlockThrottle.Load(_path).FailedAttempts);
    }

    [Fact]
    public void Unlock_WhileThrottled_ReturnsThrottled_WithoutRequestingPassword()
    {
        NewService().Create(Pwd("right-pw-123")).KeyPair.Dispose();
        WriteLockout(failedAttempts: 5); // well past the free attempts, timestamp = now

        var result = NewService().Unlock(
            () => throw new Xunit.Sdk.XunitException("password should not be requested while throttled"));

        var throttled = Assert.IsType<UnlockResult.Throttled>(result);
        Assert.True(throttled.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Unlock_PinMismatch_ReturnsMismatch_WithoutRequestingPassword()
    {
        // Enrol a pin for one key, then swap the file for a different key's wallet.
        using var enrolled = KeyPair.Generate();
        _pins.Set(Path.GetFullPath(_path), WalletPin.Compute(enrolled.PublicKeyBase64));

        using var swappedIn = KeyPair.Generate();
        WalletFile.Create(swappedIn, Pwd("attacker-pw-123")).Save(_path);

        var result = NewService().Unlock(
            () => throw new Xunit.Sdk.XunitException("password should not be requested on a pin mismatch"));

        var mismatch = Assert.IsType<UnlockResult.PinMismatch>(result);
        Assert.Equal(WalletPin.Compute(enrolled.PublicKeyBase64), mismatch.PinnedHash);
        Assert.Equal(WalletPin.Compute(swappedIn.PublicKeyBase64), mismatch.ActualHash);
    }

    [Fact]
    public void Unlock_ZeroesThePasswordFromItsProvider()
    {
        NewService().Create(Pwd("zeroed-pw-123")).KeyPair.Dispose();

        char[]? captured = null;
        var result = NewService().Unlock(() => captured = Pwd("zeroed-pw-123"));
        ((UnlockResult.Success)result).KeyPair.Dispose();

        Assert.NotNull(captured);
        Assert.All(captured!, c => Assert.Equal('\0', c));
    }

    // --- Recovery phrase ---------------------------------------------------

    [Fact]
    public void CreateFromRecoveryPhrase_IsDeterministic_AndWritesWallet()
    {
        var phrase = WalletService.GenerateRecoveryPhrase();

        var first = NewService().CreateFromRecoveryPhrase(phrase, Pwd("phrase-pw-123"));
        var firstPub = first.PublicKeyBase64;
        first.KeyPair.Dispose();

        var otherPath = Path.Combine(_dir, "wallet2.json");
        var second = new WalletService(otherPath, new InMemoryPinStore())
            .CreateFromRecoveryPhrase(phrase, Pwd("different-pw-456"));
        var secondPub = second.PublicKeyBase64;
        second.KeyPair.Dispose();

        Assert.Equal(firstPub, secondPub); // same phrase -> same key, regardless of password
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void DeriveFromRecoveryPhrase_DoesNotTouchDisk()
    {
        using var key = NewService().DeriveFromRecoveryPhrase(WalletService.GenerateRecoveryPhrase());

        Assert.False(File.Exists(_path));
        Assert.Null(_pins.TryGet(Path.GetFullPath(_path)));
    }

    [Fact]
    public void DeriveFromRecoveryPhrase_InvalidPhrase_Throws()
    {
        Assert.Throws<FormatException>(
            () => NewService().DeriveFromRecoveryPhrase("not even close to a valid twelve word phrase here ok"));
    }

    [Fact]
    public void DeriveThenSave_RoundTripsThroughUnlock()
    {
        var service = NewService();
        var phrase = WalletService.GenerateRecoveryPhrase();

        using (var derived = service.DeriveFromRecoveryPhrase(phrase))
            service.Save(derived, Pwd("save-pw-123"), fromRecoveryPhrase: true);

        var result = NewService().Unlock(() => Pwd("save-pw-123"));
        var success = Assert.IsType<UnlockResult.Success>(result);
        success.KeyPair.Dispose();

        using var reference = service.DeriveFromRecoveryPhrase(phrase);
        Assert.Equal(reference.PublicKeyBase64, success.KeyPair.PublicKeyBase64);
    }

    // --- Save / change password ------------------------------------------

    [Fact]
    public void Save_ReEncryptsUnderNewPassword_OldPasswordStopsWorking()
    {
        var created = NewService().Create(Pwd("old-pw-123"));

        NewService().Save(created.KeyPair, Pwd("new-pw-456"));
        created.KeyPair.Dispose();

        Assert.IsType<UnlockResult.WrongPassword>(NewService().Unlock(() => Pwd("old-pw-123")));

        var ok = NewService().Unlock(() => Pwd("new-pw-456"));
        Assert.IsType<UnlockResult.Success>(ok);
        ((UnlockResult.Success)ok).KeyPair.Dispose();
    }

    [Fact]
    public void Save_DoesNotDisposeTheCallersKey()
    {
        using var key = KeyPair.Generate();

        NewService().Save(key, Pwd("save-pw-123"));

        // Still usable -> not disposed out from under the caller.
        Assert.NotEmpty(key.Sign(Encoding.UTF8.GetBytes("still alive")));
    }

    // --- Inspect ---------------------------------------------------------

    [Fact]
    public void TryInspect_NoFile_ReturnsNull()
    {
        Assert.Null(NewService().TryInspect());
    }

    [Fact]
    public void TryInspect_ReportsPublicKeyAndPinCheck()
    {
        var created = NewService().Create(Pwd("inspect-pw-123"));
        created.KeyPair.Dispose();

        var inspection = NewService().TryInspect();

        Assert.NotNull(inspection);
        Assert.Equal(created.PublicKeyBase64, inspection!.PublicKeyBase64);
        Assert.Equal(PinCheck.Match, inspection.PinCheck);
    }

    // --- helpers -------------------------------------------------------------

    private void WriteLockout(int failedAttempts) =>
        File.WriteAllText(
            _path + ".lockout",
            JsonSerializer.Serialize(new UnlockThrottle
            {
                FailedAttempts = failedAttempts,
                LastFailureUtc = DateTime.UtcNow
            }));
}
