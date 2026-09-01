using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using Svrn7.Trust.KeyWallet;
using Xunit;

namespace KeyWallet.Tests;

public class DiagnosticsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public DiagnosticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KeyWalletDiagTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "wallet.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static char[] Pwd(string s) => s.ToCharArray();

    private sealed record Measurement(string Instrument, double Value, Dictionary<string, object?> Tags);

    private static (MeterListener Listener, List<Measurement> Seen) StartMeterCapture()
    {
        var seen = new List<Measurement>();
        var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == KeyWalletDiagnostics.SourceName)
                    l.EnableMeasurementEvents(inst);
            }
        };

        void Record<T>(Instrument inst, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var d = new Dictionary<string, object?>();
            foreach (var t in tags) d[t.Key] = t.Value;
            seen.Add(new Measurement(inst.Name, Convert.ToDouble(value), d));
        }

        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        listener.Start();
        return (listener, seen);
    }

    [Fact]
    public void Unlock_Success_RecordsKdfDurationWithSuccessResult()
    {
        using var keyPair = KeyPair.Generate();
        WalletFile.Create(keyPair, Pwd("diag-password-123")).Save(_path);
        var loaded = WalletFile.Load(_path);

        var (listener, seen) = StartMeterCapture();
        using (listener)
        {
            using var unlocked = loaded.Unlock(Pwd("diag-password-123"));
        }

        var kdf = seen.Where(m => m.Instrument == "keywallet.unlock.kdf.duration").ToList();
        Assert.Contains(kdf, m =>
            Equals(m.Tags[KeyWalletDiagnostics.TagResult], KeyWalletResult.Success) &&
            Equals(m.Tags[KeyWalletDiagnostics.TagKdf], "argon2id") &&
            m.Value >= 0);
    }

    [Fact]
    public void Unlock_WrongPassword_RecordsKdfDurationWithAuthFailedResult()
    {
        using var keyPair = KeyPair.Generate();
        WalletFile.Create(keyPair, Pwd("diag-password-123")).Save(_path);
        var loaded = WalletFile.Load(_path);

        var (listener, seen) = StartMeterCapture();
        using (listener)
        {
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
                () => loaded.Unlock(Pwd("wrong-password-xxx")));
        }

        Assert.Contains(seen, m =>
            m.Instrument == "keywallet.unlock.kdf.duration" &&
            Equals(m.Tags[KeyWalletDiagnostics.TagResult], KeyWalletResult.AuthFailed));
    }

    [Fact]
    public void PinnedWallet_Load_RecordsPinCheckResult()
    {
        using var keyPair = KeyPair.Generate();
        WalletFile.Create(keyPair, Pwd("diag-password-123")).Save(_path);

        var store = new InMemoryPinStore();
        store.Set("w", WalletPin.Compute(keyPair.PublicKeyBase64));

        var (listener, seen) = StartMeterCapture();
        using (listener)
        {
            PinnedWallet.Load(_path, "w", store);
        }

        Assert.Contains(seen, m =>
            m.Instrument == "keywallet.pin.checks" &&
            Equals(m.Tags[KeyWalletDiagnostics.TagPinResult], "match"));
    }

    [Fact]
    public void ActivitySource_IsNamedKeyWallet()
    {
        Assert.Equal("KeyWallet", KeyWalletDiagnostics.SourceName);
        Assert.Equal("KeyWallet", KeyWalletDiagnostics.ActivitySource.Name);
        Assert.Equal("KeyWallet", KeyWalletDiagnostics.Meter.Name);
    }

    [Fact]
    public void NoListener_UnlockStillWorks()
    {
        // With nothing attached, instrumentation must be a transparent no-op.
        using var keyPair = KeyPair.Generate();
        WalletFile.Create(keyPair, Pwd("diag-password-123")).Save(_path);

        using var unlocked = WalletFile.Load(_path).Unlock(Pwd("diag-password-123"));
        Assert.Equal(keyPair.PublicKeyBase64, unlocked.PublicKeyBase64);
    }
}
