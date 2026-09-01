using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Svrn7.Trust.KeyWallet;

/// <summary>
/// Instrumentation points only: one <see cref="System.Diagnostics.ActivitySource"/>
/// and one <see cref="System.Diagnostics.Metrics.Meter"/>, both named
/// "KeyWallet", plus the instruments hung off them.
///
/// Nothing here exports anything. With no <c>ActivityListener</c> /
/// <c>MeterListener</c> attached -- the default for a standalone run --
/// <see cref="System.Diagnostics.ActivitySource.StartActivity(string)"/>
/// returns null and every <c>Add</c>/<c>Record</c> is a no-op, so the cost
/// is negligible. A host that embeds KeyWallet opts in by pointing its own
/// OpenTelemetry (or other) pipeline at these two source names; KeyWallet
/// itself takes no telemetry dependency.
///
/// ATTRIBUTE HYGIENE -- enforced by only ever passing the constants below
/// as tag keys and enum-like strings / the wallet format version as values:
/// tags never carry a password, private key, seed, mnemonic, public key,
/// signature, wallet path, or any per-user identifier. The KDF-duration
/// histogram deliberately measures the Argon2id/PBKDF2 work factor, which
/// is already public (its parameters are stored in the wallet blob). Do
/// not add finer-grained timers around the password bytes themselves --
/// that is how a metric becomes a side channel.
/// </summary>
public static class KeyWalletDiagnostics
{
    public const string SourceName = "KeyWallet";

    private static readonly string AssemblyVersion =
        typeof(KeyWalletDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public static readonly ActivitySource ActivitySource = new(SourceName, AssemblyVersion);
    public static readonly Meter Meter = new(SourceName, AssemblyVersion);

    // Tag keys -- the ONLY strings that may be used as tag names.
    public const string TagResult = "keywallet.result";
    public const string TagKdf = "keywallet.kdf";
    public const string TagWalletVersion = "keywallet.wallet.version";
    public const string TagWithRecoveryPhrase = "keywallet.with_recovery_phrase";
    public const string TagPinResult = "keywallet.pin.result";

    // Instruments.
    public static readonly Counter<long> WalletsCreated = Meter.CreateCounter<long>(
        "keywallet.wallets_created", unit: "{wallet}",
        description: "Wallets written to disk (create, create-with-phrase, recover, change-password).");

    public static readonly Counter<long> UnlockTotal = Meter.CreateCounter<long>(
        "keywallet.unlock.total", unit: "{attempt}",
        description: "Unlock attempts, tagged by result (success, wrong_password, throttled, pin_mismatch).");

    public static readonly Histogram<double> UnlockKdfDuration = Meter.CreateHistogram<double>(
        "keywallet.unlock.kdf.duration", unit: "ms",
        description: "Wall-clock time for the password KDF + AES-GCM open during an unlock attempt.");

    public static readonly Counter<long> SignTotal = Meter.CreateCounter<long>(
        "keywallet.sign.total", unit: "{signature}",
        description: "Sign operations, tagged by result (success, locked, error).");

    public static readonly Counter<long> PinChecks = Meter.CreateCounter<long>(
        "keywallet.pin.checks", unit: "{check}",
        description: "Public-key pin checks on wallet load, tagged by result (match, first_use, mismatch).");

    // --- Recording helpers -------------------------------------------------
    //
    // The one place that knows how each operation's result maps onto its
    // counter, its activity tag, and its span status. WalletService and any
    // embedding host call these so the telemetry shape stays identical
    // regardless of who drives the operation. Values passed as `result` must
    // come from KeyWalletResult (low-cardinality, no user data -- see the
    // attribute-hygiene note above).

    /// <summary>
    /// Bumps <see cref="UnlockTotal"/> and stamps <paramref name="activity"/>
    /// (if any) with the result tag and an Ok/Error status.
    /// </summary>
    public static void RecordUnlockResult(Activity? activity, string result) =>
        RecordResult(UnlockTotal, activity, result);

    /// <summary>
    /// Bumps <see cref="SignTotal"/> and stamps <paramref name="activity"/>
    /// (if any) with the result tag and an Ok/Error status.
    /// </summary>
    public static void RecordSignResult(Activity? activity, string result) =>
        RecordResult(SignTotal, activity, result);

    /// <summary>Bumps <see cref="WalletsCreated"/>, tagged by whether the write came from a recovery-phrase flow.</summary>
    public static void RecordWalletWritten(bool withRecoveryPhrase) =>
        WalletsCreated.Add(1, new KeyValuePair<string, object?>(TagWithRecoveryPhrase, withRecoveryPhrase));

    private static void RecordResult(Counter<long> counter, Activity? activity, string result)
    {
        counter.Add(1, new KeyValuePair<string, object?>(TagResult, result));
        activity?.SetTag(TagResult, result);
        activity?.SetStatus(result == KeyWalletResult.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }
}

/// <summary>Fixed set of low-cardinality values for <see cref="KeyWalletDiagnostics.TagResult"/>.</summary>
public static class KeyWalletResult
{
    public const string Success = "success";
    public const string WrongPassword = "wrong_password";
    public const string Throttled = "throttled";
    public const string PinMismatch = "pin_mismatch";
    public const string AuthFailed = "auth_failed";
    public const string Locked = "locked";
    public const string Error = "error";
}
