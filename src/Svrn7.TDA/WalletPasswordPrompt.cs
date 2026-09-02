namespace Svrn7.TDA;

/// <summary>
/// Acquires the wallet password (docs/AGENTWALLET.md §D13):
/// <list type="number">
///   <item><c>PANDO_WALLET_PASSWORD</c> if set → used as-is, no confirmation.</item>
///   <item>else an interactive prompt; on first-run <b>creation</b> it is entered twice and must match.</item>
///   <item>if stdin is not interactive <b>and</b> the env var is absent → fail fast (never block on an unanswerable prompt).</item>
/// </list>
/// The returned <c>char[]</c> is the caller's to zero after use.
/// </summary>
public static class WalletPasswordPrompt
{
    public const string EnvVar = "PANDO_WALLET_PASSWORD";

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
            Environment.Exit(1);
        }

        var first = ReadHidden(firstRunCreate ? "Create wallet password: " : "Wallet password: ");
        if (first.Length == 0)
        {
            Console.Error.WriteLine("ERROR: empty password.");
            Environment.Exit(1);
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
                Environment.Exit(1);
            }
        }

        return first;
    }

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
                    if (buf.Count > 0) buf.RemoveAt(buf.Count - 1);
                    break;
                default:
                    if (!char.IsControl(key.KeyChar)) buf.Add(key.KeyChar);
                    break;
            }
        }
    }
}
