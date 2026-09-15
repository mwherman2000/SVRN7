using System.Security.Cryptography;
using System.Text.Json;
using Svrn7.Trust.AgentWallet;

namespace AgentWallet.Tests;

public class AgentWalletFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AgentWalletFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AgentWalletFileTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "agent-identity.wallet");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static char[] Pw(string s) => s.ToCharArray();

    [Fact]
    public void Save_ThenLoad_RoundTripsHeader()
    {
        var file = new AgentWalletFile
        {
            Version = 1,
            Secp256k1PublicKeyHex = "02abc",
            EncryptedPayloadBase64 = "AAAA",
            CreatedUtc = "2026-09-01T00:00:00.0000000+00:00"
        };
        file.Save(_path);

        var back = AgentWalletFile.Load(_path);
        Assert.Equal(file.Version, back.Version);
        Assert.Equal(file.Secp256k1PublicKeyHex, back.Secp256k1PublicKeyHex);
        Assert.Equal(file.EncryptedPayloadBase64, back.EncryptedPayloadBase64);
        Assert.Equal(file.CreatedUtc, back.CreatedUtc);
    }

    [Fact]
    public void Save_SecondTime_KeepsPreviousAtBak()
    {
        new AgentWalletFile { Secp256k1PublicKeyHex = "first" }.Save(_path);
        new AgentWalletFile { Secp256k1PublicKeyHex = "second" }.Save(_path);

        Assert.True(File.Exists(_path + ".bak"));
        Assert.Contains("first", File.ReadAllText(_path + ".bak"));
        Assert.Contains("second", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Load_CorruptFile_ThrowsWithHint()
    {
        File.WriteAllText(_path, "{ not json");

        var ex = Assert.Throws<InvalidDataException>(() => AgentWalletFile.Load(_path));
        Assert.Contains(_path, ex.Message);
    }

    [Fact]
    public void Header_PublicKey_IsCleartext()
    {
        var identity = MakeWallet(out _);

        var raw = File.ReadAllText(_path);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(identity.Secp256k1PublicKeyHex,
            doc.RootElement.GetProperty("secp256k1PublicKeyHex").GetString());
        // the private key must NOT appear anywhere in the file text
        Assert.DoesNotContain(Convert.ToHexString(identity.Secp256k1PrivateKey).ToLowerInvariant(), raw);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTripsPayload_And_WrongPasswordThrows()
    {
        MakeWallet(out var phrase);
        var file = AgentWalletFile.Load(_path);

        // Right password: internal Decrypt is exercised via the service in other
        // tests; here assert the raw crypto contract through a fresh wallet.
        var svc = new AgentWalletService(_path, new NullPinStore());
        var unlocked = svc.Unlock(() => Pw("pw-123"));
        var ok = Assert.IsType<AgentUnlockResult.Success>(unlocked);
        Assert.Equal(phrase, svc.ExportRecoveryPhrase(() => Pw("pw-123")));
        ok.Identity.Dispose();

        var bad = svc.Unlock(() => Pw("nope"));
        Assert.IsType<AgentUnlockResult.WrongPassword>(bad);
    }

    private AgentIdentity MakeWallet(out string phrase)
    {
        var svc = new AgentWalletService(_path, new NullPinStore());
        var id = svc.Create(Pw("pw-123"), h => $"did:drn:test/{h}", "Wanderer");
        phrase = svc.ExportRecoveryPhrase(() => Pw("pw-123"))!;
        return id;
    }
}
