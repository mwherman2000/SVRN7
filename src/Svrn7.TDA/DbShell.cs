using System.Text.Json;
using LiteDB;
using Svrn7.Trust.AgentWallet;
using StjSerializer = System.Text.Json.JsonSerializer;
using LiteJson = LiteDB.JsonSerializer;

namespace Svrn7.TDA;

/// <summary>
/// <c>Svrn7.TDA.dll db-shell</c> — read an instance's encrypted LiteDB databases
/// after unlocking its wallet (docs/AGENTWALLET.md §D9). Since every
/// <c>svrn7-*.db</c> is AES-encrypted with a key that only comes out of the
/// wallet, LiteDB Studio and the <c>litedb</c> CLI can no longer open them; this
/// is the sanctioned inspection path.
///
/// <code>
///   Svrn7.TDA.dll db-shell --name &lt;n&gt; [--did &lt;d&gt;] [--data-root &lt;p&gt;]
///                          [--db dids|schemas|main|msg|vcs|all]
///                          [--collection &lt;C&gt;] [--sql "&lt;litedb sql&gt;"] [--limit N]
/// </code>
///
/// No <c>--collection</c>/<c>--sql</c> → list collections and document counts.
/// Read-only in intent; it opens the file read/write (LiteDB has no read-only
/// mode) but never writes.
/// </summary>
internal static class DbShell
{
    private static readonly Dictionary<string, string> DbFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dids"]    = "svrn7-dids.db",
        ["schemas"] = "svrn7-schemas.db",
        ["main"]    = "svrn7.db",
        ["msg"]     = "svrn7-msg.db",
        ["vcs"]     = "svrn7-vcs.db",
    };

    public static int Run(string[] args)
    {
        string? Arg(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var name       = Arg("--name");
        var did         = Arg("--did");
        var dataRootArg = Arg("--data-root");
        var dbSel       = (Arg("--db") ?? "all").ToLowerInvariant();
        var collection  = Arg("--collection");
        var sql         = Arg("--sql");
        var limit       = int.TryParse(Arg("--limit"), out var l) ? l : 20;

        if (name is null && did is null)
        {
            Console.Error.WriteLine("db-shell: --name <instance> (or --did <did>) is required.");
            return 1;
        }

        var dataRoot = PandoPaths.ResolveDataRoot(dataRootArg);
        var match = PandoPaths.EnumerateInstances(dataRoot).FirstOrDefault(x =>
            (did  is not null && string.Equals(x.Meta.Did,  did,  StringComparison.Ordinal)) ||
            (name is not null && string.Equals(x.Meta.Name, name, StringComparison.OrdinalIgnoreCase)));
        if (match.Dir is null)
        {
            Console.Error.WriteLine($"db-shell: no instance for '{did ?? name}' under {dataRoot}.");
            return 1;
        }

        var (pinStore, _) = (Svrn7.Trust.AgentWallet.PinStores.CreateDefault().Store, 0);
        var svc = new AgentWalletService(PandoPaths.WalletPath(match.Dir), pinStore);

        var password = WalletPasswordPrompt.Acquire(firstRunCreate: false);
        string dbPassword;
        try
        {
            var unlock = svc.Unlock(() => (char[])password.Clone());
            if (unlock is not AgentUnlockResult.Success ok)
            {
                Console.Error.WriteLine($"db-shell: unlock failed — {unlock.GetType().Name}.");
                return 1;
            }
            using (ok.Identity)
                dbPassword = ok.Identity.DatabasePassword();
        }
        finally
        {
            Array.Clear(password);
        }

        var memDir = PandoPaths.MemDir(match.Dir);
        var targets = dbSel == "all"
            ? DbFiles.Keys.ToArray()
            : DbFiles.ContainsKey(dbSel) ? [dbSel] : throw new ArgumentException($"unknown --db '{dbSel}'");

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

        foreach (var key in targets)
        {
            var path = Path.Combine(memDir, DbFiles[key]);
            Console.WriteLine($"── {key}  ({DbFiles[key]}) ──────────────────────────────────────");
            if (!File.Exists(path))
            {
                Console.WriteLine("  (not created yet)");
                continue;
            }

            using var db = new LiteDatabase($"Filename=\"{path}\";Password={dbPassword};ReadOnly=false");

            if (sql is not null && (dbSel != "all" || targets.Length == 1))
            {
                using var reader = db.Execute(sql);
                var n = 0;
                while (reader.Read() && n++ < limit)
                    Console.WriteLine(BsonToJson(reader.Current, jsonOpts));
                continue;
            }

            if (collection is not null && (dbSel != "all" || targets.Length == 1))
            {
                var col = db.GetCollection(collection);
                var n = 0;
                foreach (var doc in col.FindAll())
                {
                    if (n++ >= limit) { Console.WriteLine($"  … ({col.Count()} total, showing {limit})"); break; }
                    Console.WriteLine(BsonToJson(doc, jsonOpts));
                }
                continue;
            }

            foreach (var cname in db.GetCollectionNames().OrderBy(x => x))
                Console.WriteLine($"  {cname,-28} {db.GetCollection(cname).Count(),8} doc(s)");
        }

        return 0;
    }

    private static string BsonToJson(BsonValue v, JsonSerializerOptions opts)
    {
        // LiteDB's own JSON serializer keeps $-prefixed type hints; re-parse
        // through System.Text.Json for readable, indented output.
        var raw = LiteJson.Serialize(v);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return StjSerializer.Serialize(doc.RootElement, opts);
        }
        catch
        {
            return raw;
        }
    }
}
