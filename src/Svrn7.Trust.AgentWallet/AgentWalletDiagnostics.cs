using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Svrn7.Trust.AgentWallet;

/// <summary>
/// Instrumentation points only: one <see cref="ActivitySource"/> and one
/// <see cref="Meter"/>, both named "AgentWallet", plus the instruments hung off
/// them. Nothing here exports anything — with no listener attached,
/// <see cref="ActivitySource.StartActivity(string)"/> returns null and every
/// <c>Add</c>/<c>Record</c> is a no-op. A host opts in by pointing its own
/// OpenTelemetry pipeline at the source names
/// (<c>AddSource("AgentWallet")</c> / <c>AddMeter("AgentWallet")</c>).
///
/// Attribute hygiene: tags only ever carry the constants below as keys and the
/// low-cardinality strings in <see cref="AgentWalletResult"/> as values — never
/// a password, private key, seed, mnemonic, public key, wallet path, or per-user
/// identifier. The KDF-duration histogram deliberately measures the already-
/// public Argon2id work factor.
/// </summary>
public static class AgentWalletDiagnostics
{
    public const string SourceName = "AgentWallet";

    private static readonly string AssemblyVersion =
        typeof(AgentWalletDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public static readonly ActivitySource ActivitySource = new(SourceName, AssemblyVersion);
    public static readonly Meter Meter = new(SourceName, AssemblyVersion);

    // Tag keys — the ONLY strings that may be used as tag names.
    public const string TagResult = "agentwallet.result";
    public const string TagOperation = "agentwallet.operation";
    public const string TagPinResult = "agentwallet.pin.result";

    public static readonly Counter<long> WalletsWritten = Meter.CreateCounter<long>(
        "agentwallet.wallets_written", unit: "{wallet}",
        description: "Wallet files written to disk, tagged by operation (create, restore, change_password, rotate_db_key).");

    public static readonly Counter<long> UnlockTotal = Meter.CreateCounter<long>(
        "agentwallet.unlock.total", unit: "{attempt}",
        description: "Unlock attempts, tagged by result (success, wrong_password, throttled, pin_mismatch).");

    public static readonly Histogram<double> UnlockKdfDuration = Meter.CreateHistogram<double>(
        "agentwallet.unlock.kdf.duration", unit: "ms",
        description: "Wall-clock time for the password KDF + AES-GCM open during an unlock attempt.");

    public static readonly Counter<long> PinChecks = Meter.CreateCounter<long>(
        "agentwallet.pin.checks", unit: "{check}",
        description: "Public-key pin checks on wallet load, tagged by result (match, first_use, mismatch).");

    public static void RecordUnlockResult(Activity? activity, string result)
    {
        UnlockTotal.Add(1, new KeyValuePair<string, object?>(TagResult, result));
        activity?.SetTag(TagResult, result);
        activity?.SetStatus(result == AgentWalletResult.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    public static void RecordWalletWritten(string operation) =>
        WalletsWritten.Add(1, new KeyValuePair<string, object?>(TagOperation, operation));

    public static void RecordPinCheck(PinCheck check) =>
        PinChecks.Add(1, new KeyValuePair<string, object?>(
            TagPinResult,
            check switch
            {
                PinCheck.Match => "match",
                PinCheck.Mismatch => "mismatch",
                _ => "first_use"
            }));

    public static void RecordKdfDuration(long startTimestamp, string result) =>
        UnlockKdfDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>(TagResult, result));
}

/// <summary>Fixed set of low-cardinality values for <see cref="AgentWalletDiagnostics.TagResult"/>.</summary>
public static class AgentWalletResult
{
    public const string Success = "success";
    public const string WrongPassword = "wrong_password";
    public const string Throttled = "throttled";
    public const string PinMismatch = "pin_mismatch";
    public const string NoWallet = "no_wallet";
}
