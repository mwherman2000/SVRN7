using Svrn7.Trust.AgentWallet;

namespace AgentWallet.Tests;

public class AgentWalletServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AgentWalletServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AgentWalletServiceTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "agent-identity.wallet");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static char[] Pw(string s) => s.ToCharArray();
    private AgentWalletService NewService(IPinStore? pin = null) => new(_path, pin ?? new NullPinStore());

    private static string WandererDid(string genesisHash) =>
        $"did:drn:wanderer.svrn7.net/agent/1.0/{genesisHash}";

    // ── Create ─────────────────────────────────────────────────────────────

    [Fact]
    public void Create_WritesFile_AndReturnsUnlockedIdentity()
    {
        var svc = NewService();

        using var id = svc.Create(Pw("pw"), WandererDid, "Wanderer");

        Assert.True(File.Exists(_path));
        Assert.Equal(64, id.GenesisHashHex.Length);
        Assert.Equal(WandererDid(id.GenesisHashHex), id.Did);
        Assert.Equal("Wanderer", id.Role);
        Assert.Equal(32, id.Secp256k1PrivateKey.Length);
        Assert.Equal(32, id.X25519PrivateKey.Length);
        Assert.Equal(32, id.DbMasterKey.Length);
        Assert.Equal(64, id.DatabasePassword().Length);
        Assert.Equal(id.GenesisHashHex, GenesisHash.Compute(id.Secp256k1PublicKeyHex));
    }

    [Fact]
    public void Create_WhenWalletExists_Throws()
    {
        var svc = NewService();
        svc.Create(Pw("pw"), WandererDid, "Wanderer").Dispose();

        Assert.Throws<InvalidOperationException>(() => svc.Create(Pw("pw"), WandererDid, "Wanderer"));
    }

    [Fact]
    public void Create_FromRecoveryPhrase_ReproducesSameIdentity()
    {
        var phrase = RecoveryPhrase.Generate();

        var a = NewService().Create(Pw("pw"), WandererDid, "Wanderer", recoveryPhrase: phrase);
        var aDid = a.Did;
        var aSecpPub = a.Secp256k1PublicKeyHex;
        var aX = a.X25519PublicKeyHex;
        a.Dispose();

        File.Delete(_path);
        File.Delete(_path + ".bak");

        using var b = NewService().Create(Pw("different-pw"), WandererDid, "Citizen", recoveryPhrase: phrase);

        Assert.Equal(aDid, b.Did);            // DID follows the key, not the password or role
        Assert.Equal(aSecpPub, b.Secp256k1PublicKeyHex);
        Assert.Equal(aX, b.X25519PublicKeyHex);
    }

    // ── Unlock ─────────────────────────────────────────────────────────────

    [Fact]
    public void Unlock_NoWallet_ReturnsNoWallet()
    {
        var r = NewService().Unlock(() => Pw("pw"));
        Assert.IsType<AgentUnlockResult.NoWallet>(r);
    }

    [Fact]
    public void Unlock_CorrectPassword_ReturnsSameKeys()
    {
        var svc = NewService();
        AgentIdentity created = svc.Create(Pw("pw"), WandererDid, "Wanderer");
        var createdSecp = Convert.ToHexString(created.Secp256k1PrivateKey);
        var createdX = Convert.ToHexString(created.X25519PrivateKey);
        var createdDbPw = created.DatabasePassword();
        created.Dispose();

        var r = svc.Unlock(() => Pw("pw"));
        using var id = Assert.IsType<AgentUnlockResult.Success>(r).Identity;

        Assert.Equal(createdSecp, Convert.ToHexString(id.Secp256k1PrivateKey));
        Assert.Equal(createdX, Convert.ToHexString(id.X25519PrivateKey));
        Assert.Equal(createdDbPw, id.DatabasePassword());
    }

    [Fact]
    public void Unlock_WrongPassword_RecordsThrottleFailure()
    {
        var svc = NewService();
        svc.Create(Pw("pw"), WandererDid, "Wanderer").Dispose();

        Assert.IsType<AgentUnlockResult.WrongPassword>(svc.Unlock(() => Pw("bad")));
        Assert.True(File.Exists(_path + ".lockout"));
    }

    [Fact]
    public void Unlock_AfterEnoughFailures_ReturnsThrottled()
    {
        var svc = NewService();
        svc.Create(Pw("pw"), WandererDid, "Wanderer").Dispose();

        for (var i = 0; i < 3; i++) svc.Unlock(() => Pw("bad"));

        var r = svc.Unlock(() => Pw("pw")); // correct, but throttled before the password is checked
        var throttled = Assert.IsType<AgentUnlockResult.Throttled>(r);
        Assert.True(throttled.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Unlock_Success_ResetsThrottle()
    {
        var svc = NewService();
        svc.Create(Pw("pw"), WandererDid, "Wanderer").Dispose();

        svc.Unlock(() => Pw("bad"));
        svc.Unlock(() => Pw("bad"));
        Assert.IsType<AgentUnlockResult.Success>(svc.Unlock(() => Pw("pw"))).Identity.Dispose();

        Assert.False(File.Exists(_path + ".lockout"));
    }

    // ── Pinning ────────────────────────────────────────────────────────────

    [Fact]
    public void Unlock_PinnedOnFirstUse_ThenMismatchAfterFileSwap()
    {
        var pin = new InMemoryPinStore();

        var svc = NewService(pin);
        svc.Create(Pw("pw"), WandererDid, "Wanderer").Dispose();          // Create pins
        Assert.IsType<AgentUnlockResult.Success>(svc.Unlock(() => Pw("pw"))).Identity.Dispose();

        // Replace the wallet with a foreign one at the same path.
        File.Delete(_path);
        File.Delete(_path + ".bak");
        var otherDir = Path.Combine(_dir, "other");
        Directory.CreateDirectory(otherDir);
        var foreign = new AgentWalletService(_path, new NullPinStore());
        foreign.Create(Pw("pw"), WandererDid, "Wanderer").Dispose();

        var r = svc.Unlock(() => Pw("pw"));
        Assert.IsType<AgentUnlockResult.PinMismatch>(r);
    }

    // ── ChangePassword ────────────────────────────────────────────────────

    [Fact]
    public void ChangePassword_NewWorks_OldFails_KeysUnchanged()
    {
        var svc = NewService();
        var before = svc.Create(Pw("old"), WandererDid, "Wanderer");
        var beforeSecp = Convert.ToHexString(before.Secp256k1PrivateKey);
        var beforeDbPw = before.DatabasePassword();
        before.Dispose();

        svc.ChangePassword(() => Pw("old"), Pw("new"));

        Assert.IsType<AgentUnlockResult.WrongPassword>(svc.Unlock(() => Pw("old")));
        using var after = Assert.IsType<AgentUnlockResult.Success>(svc.Unlock(() => Pw("new"))).Identity;
        Assert.Equal(beforeSecp, Convert.ToHexString(after.Secp256k1PrivateKey));
        Assert.Equal(beforeDbPw, after.DatabasePassword()); // DB master key survives a password change
    }

    [Fact]
    public void ChangePassword_WrongCurrent_Throws_AndLeavesWalletUsable()
    {
        var svc = NewService();
        svc.Create(Pw("old"), WandererDid, "Wanderer").Dispose();

        Assert.Throws<UnauthorizedAccessException>(() => svc.ChangePassword(() => Pw("wrong"), Pw("new")));
        Assert.IsType<AgentUnlockResult.Success>(svc.Unlock(() => Pw("old"))).Identity.Dispose();
    }

    // ── RotateDatabaseKey ─────────────────────────────────────────────────

    [Fact]
    public void RotateDatabaseKey_ChangesDbKey_KeepsIdentityKeys()
    {
        var svc = NewService();
        var id = svc.Create(Pw("pw"), WandererDid, "Wanderer");
        var oldDbPw = id.DatabasePassword();
        var secpPub = id.Secp256k1PublicKeyHex;
        id.Dispose();

        var rot = svc.RotateDatabaseKey(() => Pw("pw"));
        Assert.NotEqual(Convert.ToHexString(rot.OldKey), Convert.ToHexString(rot.NewKey));

        using var after = Assert.IsType<AgentUnlockResult.Success>(svc.Unlock(() => Pw("pw"))).Identity;
        Assert.Equal(Convert.ToHexString(rot.NewKey).ToLowerInvariant(), after.DatabasePassword());
        Assert.NotEqual(oldDbPw, after.DatabasePassword());
        Assert.Equal(secpPub, after.Secp256k1PublicKeyHex); // identity key untouched
    }

    // ── ExportRecoveryPhrase / Inspect ───────────────────────────────────

    [Fact]
    public void ExportRecoveryPhrase_RoundTrips()
    {
        var phrase = RecoveryPhrase.Generate();
        var svc = NewService();
        svc.Create(Pw("pw"), WandererDid, "Wanderer", recoveryPhrase: phrase).Dispose();

        Assert.Equal(phrase, svc.ExportRecoveryPhrase(() => Pw("pw")));
        RecoveryPhrase.Validate(svc.ExportRecoveryPhrase(() => Pw("pw"))!);
    }

    [Fact]
    public void Inspect_ReturnsHeader_WithoutPassword()
    {
        var svc = NewService(new InMemoryPinStore());
        using var id = svc.Create(Pw("pw"), WandererDid, "Wanderer");

        var info = svc.Inspect();
        Assert.NotNull(info);
        Assert.Equal(id.Secp256k1PublicKeyHex, info!.Secp256k1PublicKeyHex);
        Assert.Equal(id.GenesisHashHex, info.GenesisHashHex);
        Assert.Equal(PinCheck.Match, info.PinCheck);
    }

    [Fact]
    public void Inspect_NoWallet_ReturnsNull()
    {
        Assert.Null(NewService().Inspect());
    }
}
