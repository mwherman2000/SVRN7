using System.Text.Json;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// Slows down repeated wrong-password guesses against a wallet file through
/// this app's own unlock prompt. Persisted as a small sidecar next to the
/// wallet file (path + ".lockout") so restarting the process doesn't reset
/// the count.
///
/// SCOPE NOTE: this only throttles guesses made through KeyWallet's own UI.
/// Someone who copies wallet.json to another machine and brute-forces it
/// directly bypasses this entirely -- the real defense against that is the
/// KDF cost (Argon2id/PBKDF2 in WalletCrypto), not this class. This exists
/// as defense-in-depth for the interactive-guessing scenario, not as the
/// primary control.
///
/// Uses exponential backoff with a capped delay rather than a hard lockout
/// after N attempts, deliberately: a permanent (or very long) lockout on a
/// single-user local wallet just turns a typo streak into a self-inflicted
/// denial of service, which is worse for this threat model than a bounded
/// wait.
/// </summary>
public sealed class UnlockThrottle
{
    private const int FreeAttempts = 2; // this many wrong guesses cost nothing
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
            // A corrupted throttle sidecar should never lock a user out of
            // their own wallet -- fail open to "no recorded failures".
            return new UnlockThrottle();
        }
    }

    /// <summary>Time remaining before another unlock attempt is allowed; TimeSpan.Zero if none.</summary>
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

    /// <summary>Clears any recorded failures -- call on a successful unlock or when a wallet file is (re)created.</summary>
    public static void Reset(string walletPath)
    {
        var path = SidecarPath(walletPath);
        if (File.Exists(path)) File.Delete(path);
    }
}
