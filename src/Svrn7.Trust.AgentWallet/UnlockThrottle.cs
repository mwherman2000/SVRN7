using System.Text.Json;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// Slows repeated wrong-password guesses against a wallet file made through this
/// process's own unlock path. Persisted as a sidecar next to the wallet file
/// (path + ".lockout") so a process restart does not reset the count. Copied
/// from <c>Svrn7.Trust.KeyWallet.UnlockThrottle</c> unchanged apart from the
/// namespace (docs/AGENTWALLET.md §3, §D14).
///
/// Scope: this only throttles guesses through AgentWallet's unlock path. An
/// attacker who copies the wallet file elsewhere and brute-forces it directly
/// bypasses this — the real defence there is the Argon2id cost in
/// <see cref="WalletCrypto"/>, not this class.
///
/// Exponential backoff with a capped delay, not a hard lockout: a permanent
/// lockout on a single-user local wallet just turns a typo streak into
/// self-inflicted denial of service.
/// </summary>
public sealed class UnlockThrottle
{
    private const int FreeAttempts = 2;      // this many wrong guesses cost nothing
    private const int MaxDelaySeconds = 300; // 5 minutes, then the delay stops growing

    public int FailedAttempts { get; set; }
    public DateTime LastFailureUtc { get; set; }

    private static string SidecarPath(string walletPath) => walletPath + ".lockout";

    public static UnlockThrottle Load(string walletPath)
    {
        var path = SidecarPath(walletPath);
        if (!File.Exists(path)) return new UnlockThrottle();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UnlockThrottle>(json) ?? new UnlockThrottle();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupted throttle sidecar must never lock a user out of their
            // own wallet — fail open to "no recorded failures".
            return new UnlockThrottle();
        }
    }

    /// <summary>Time remaining before another unlock attempt is allowed; <see cref="TimeSpan.Zero"/> if none.</summary>
    public TimeSpan GetRemainingWait()
    {
        if (FailedAttempts <= FreeAttempts) return TimeSpan.Zero;

        var delaySeconds = Math.Min(MaxDelaySeconds, 1 << (FailedAttempts - FreeAttempts));
        var readyAtUtc = LastFailureUtc.AddSeconds(delaySeconds);
        var remaining = readyAtUtc - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void RecordFailure(string walletPath)
    {
        FailedAttempts++;
        LastFailureUtc = DateTime.UtcNow;
        File.WriteAllText(SidecarPath(walletPath), JsonSerializer.Serialize(this));
    }

    /// <summary>Clears any recorded failures — call on a successful unlock or when a wallet file is (re)created.</summary>
    public static void Reset(string walletPath)
    {
        var path = SidecarPath(walletPath);
        if (File.Exists(path)) File.Delete(path);
    }
}
