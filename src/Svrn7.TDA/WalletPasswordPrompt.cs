using System.Diagnostics.CodeAnalysis;
using Svrn7.Trust.AgentWallet;

namespace Svrn7.TDA;

/// <summary>
/// Acquires the wallet password and, on subsequent runs, drives the whole
/// interactive unlock (docs/AGENTWALLET.md §D13):
/// <list type="number">
///   <item><c>PANDO_WALLET_PASSWORD</c> if set → used as-is, no confirmation, no retry loop.</item>
///   <item>else an interactive prompt; on first-run <b>creation</b> it is entered twice and must match.</item>
///   <item>
///     on a subsequent run, if the entered value is exactly 12 words it is
///     tried as the recovery phrase instead of the password — a match walks
///     the user through setting a brand-new password (forgot-password
///     recovery); any mismatch (wrong password, wrong phrase) reprompts.
///   </item>
///   <item>if stdin is not interactive <b>and</b> the env var is absent → fail fast (never block on an unanswerable prompt).</item>
/// </list>
/// This flow is exclusive to the TDA's own console bootstrap — it is
/// unrelated to PandoMail's <c>Svrn7.Trust.AppAuthn</c> sign-in gate
/// (<see cref="Svrn7RunspaceContext.VerifyWalletPassword"/>), which re-checks
/// the password against an already-unlocked, already-running TDA over
/// DIDComm and never touches this class.
/// </summary>
public static class WalletPasswordPrompt
{
    public const string EnvVar = "PANDO_WALLET_PASSWORD";

    /// <summary>First-run wallet creation only: acquires (and, on creation, double-confirms) the password. The returned <c>char[]</c> is the caller's to zero after use.</summary>
    public static char[] Acquire(bool firstRunCreate)
    {
        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrEmpty(env))
            return env.ToCharArray();

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                $"ERROR: {EnvVar} is not set and there is no interactive console to prompt on. " +
                "Set the environment variable or run the TDA attached to a terminal.");
            Program.ExitWithPause(1);
        }

        var first = ReadHidden(firstRunCreate ? "Create wallet password: " : "Wallet password: ");
        if (first.Length == 0)
        {
            Console.Error.WriteLine("ERROR: empty password.");
            Program.ExitWithPause(1);
        }

        if (firstRunCreate)
        {
            var confirm = ReadHidden("Confirm wallet password: ");
            var match = first.AsSpan().SequenceEqual(confirm);
            Array.Clear(confirm);
            if (!match)
            {
                Array.Clear(first);
                Console.Error.WriteLine("ERROR: passwords do not match.");
                Program.ExitWithPause(1);
            }
        }

        return first;
    }

    /// <summary>
    /// Displays a freshly-generated recovery phrase exactly once, right after
    /// the new wallet password has been confirmed, instructs the user to save
    /// it, waits for Enter, then clears the screen so the phrase does not
    /// linger in scrollback. Called only when the phrase was freshly
    /// generated (not one the user already knew via <c>--recovery-phrase</c>).
    /// </summary>
    public static void ShowAndConfirmRecoveryPhrase(string phrase)
    {
        var rule = new string('=', 78);
        Console.WriteLine();
        Console.WriteLine(rule);
        Console.WriteLine("RECOVERY PHRASE — write this down now, it is shown only once:");
        Console.WriteLine();
        Console.WriteLine($"    {phrase}");
        Console.WriteLine();
        Console.WriteLine(rule);
        Console.WriteLine();
        Console.WriteLine("Copy this phrase somewhere safe now. If you ever forget your wallet");
        Console.WriteLine("password, this phrase is the only way back in.");
        Console.WriteLine();
        Console.Write("Press Enter when you're done — the screen will then be cleared. ");
        Console.ReadLine();
        Console.Clear();
    }

    /// <summary>
    /// Subsequent-run interactive unlock. Owns the whole prompt/retry loop:
    /// wrong password or wrong recovery phrase reprompts; a correct recovery
    /// phrase walks the user through setting a new password and returns the
    /// unlocked identity under it. <see cref="AgentUnlockResult.Throttled"/>,
    /// <see cref="AgentUnlockResult.PinMismatch"/>, and
    /// <see cref="AgentUnlockResult.NoWallet"/> are hard stops (calls
    /// <see cref="Program.Die"/>, never returns). <c>$PANDO_WALLET_PASSWORD</c>,
    /// if set, is tried once non-interactively — no retry loop and no
    /// recovery-phrase detection for it, exactly as before.
    /// </summary>
    public static AgentIdentity UnlockInteractive(AgentWalletService svc)
    {
        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrEmpty(env))
        {
            var envPassword = env.ToCharArray();
            var envResult = svc.Unlock(() => (char[])envPassword.Clone());
            Array.Clear(envPassword);
            return ResolveOrDie(envResult);
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                $"ERROR: {EnvVar} is not set and there is no interactive console to prompt on. " +
                "Set the environment variable or run the TDA attached to a terminal.");
            Program.ExitWithPause(1);
        }

        while (true)
        {
            var input = ReadHidden("Wallet password: ");
            if (input.Length == 0)
            {
                Console.Error.WriteLine("ERROR: empty password.");
                Program.ExitWithPause(1);
            }

            if (TryAsRecoveryPhrase(input, out var phrase))
            {
                var phraseResult = svc.UnlockWithRecoveryPhrase(phrase);
                Array.Clear(input);
                switch (phraseResult)
                {
                    case AgentUnlockResult.Success ok:
                        Console.WriteLine();
                        Console.WriteLine("Recovery phrase verified. Choose a new wallet password.");
                        var newPassword = ReadNewPasswordWithConfirm();
                        svc.ChangePasswordUsingRecoveryPhrase(phrase, newPassword);
                        Array.Clear(newPassword);
                        return ok.Identity;

                    case AgentUnlockResult.WrongRecoveryPhrase:
                        Console.Error.WriteLine("Recovery phrase not recognized for this wallet. Try again.");
                        continue;

                    case AgentUnlockResult.RecoveryPhraseUnavailable:
                        Console.Error.WriteLine(
                            "This wallet does not support recovery-phrase unlock yet " +
                            "(unlock once with the password to enable it). Try again.");
                        continue;

                    default:
                        return ResolveOrDie(phraseResult);
                }
            }
            else
            {
                var result = svc.Unlock(() => (char[])input.Clone());
                Array.Clear(input);
                switch (result)
                {
                    case AgentUnlockResult.Success ok:
                        return ok.Identity;

                    case AgentUnlockResult.WrongPassword:
                        Console.Error.WriteLine("Wrong wallet password. Try again.");
                        continue;

                    default:
                        return ResolveOrDie(result);
                }
            }
        }
    }

    /// <summary>Exactly 12 whitespace-separated words → treat as a recovery-phrase attempt instead of a password, per the subsequent-run flow's disambiguation rule.</summary>
    private static bool TryAsRecoveryPhrase(char[] input, out string phrase)
    {
        var text = new string(input).Trim();
        var wordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount == 12)
        {
            phrase = text;
            return true;
        }

        phrase = "";
        return false;
    }

    private static char[] ReadNewPasswordWithConfirm()
    {
        while (true)
        {
            var first = ReadHidden("New wallet password: ");
            if (first.Length == 0)
            {
                Console.Error.WriteLine("ERROR: empty password. Try again.");
                continue;
            }

            var confirm = ReadHidden("Confirm new wallet password: ");
            var match = first.AsSpan().SequenceEqual(confirm);
            Array.Clear(confirm);
            if (match)
                return first;

            Array.Clear(first);
            Console.Error.WriteLine("Passwords do not match. Try again.");
        }
    }

    /// <summary>Handles every non-Success, non-retryable <see cref="AgentUnlockResult"/> exactly as the old inline switch in Program.cs did — always calls <see cref="Program.Die"/>, never returns.</summary>
    [DoesNotReturn]
    private static AgentIdentity ResolveOrDie(AgentUnlockResult result)
    {
        switch (result)
        {
            case AgentUnlockResult.WrongPassword:
                Program.Die("wrong wallet password.");
                break;
            case AgentUnlockResult.Throttled t:
                Program.Die($"wallet is locked out for another {t.RetryAfter.TotalSeconds:0}s after repeated failures.");
                break;
            case AgentUnlockResult.PinMismatch:
                Program.Die("wallet public key does not match its pin — the wallet file was replaced or rolled back.");
                break;
            case AgentUnlockResult.NoWallet nw:
                Program.Die($"no wallet at '{nw.Path}'. Run with --reset to re-bootstrap this identity.");
                break;
            default:
                Program.Die($"unexpected unlock result: {result.GetType().Name}");
                break;
        }

        throw new InvalidOperationException("unreachable");
    }

    // Echoes '*' per keystroke (erased on Backspace) instead of the raw character, so the
    // user gets typing feedback without the password itself ever touching the console buffer.
    private static char[] ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var buf = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buf.ToArray();
                case ConsoleKey.Backspace:
                    if (buf.Count > 0)
                    {
                        buf.RemoveAt(buf.Count - 1);
                        Console.Write("\b \b");
                    }
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buf.Add(key.KeyChar);
                        Console.Write('*');
                    }
                    break;
            }
        }
    }
}
