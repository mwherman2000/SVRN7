using System.Text;
using System.Text.RegularExpressions;

namespace Svrn7.TDA;

/// <summary>
/// Resolves the Web 7.0 Pando data-root layout (docs/AGENTWALLET.md §5):
///
/// <code>
/// ~/.web7-pando/                          ($PANDO_HOME | --data-root override)
/// ├── bin/                                published TDA binaries (out of scope here)
/// ├── lobe-library/                       machine-level LOBE .nupkg source
/// └── &lt;name&gt;-&lt;genesisHash[..8]&gt;/          one directory per identity
///       ├── identity.meta.json
///       ├── agent-identity.wallet (+ .bak, .lockout)
///       ├── lobes/
///       └── mem/  (svrn7-*.db, crash.log)
/// </code>
/// </summary>
public static partial class PandoPaths
{
    public const string DefaultRootName = ".web7-pando";
    public const string WalletFileName  = "agent-identity.wallet";
    public const string MetaFileName    = "identity.meta.json";
    public const string GenesisSlugLen  = "8";

    /// <summary>--data-root arg › $PANDO_HOME › %USERPROFILE%/.web7-pando.</summary>
    public static string ResolveDataRoot(string? dataRootArg)
    {
        if (!string.IsNullOrWhiteSpace(dataRootArg))
            return Path.GetFullPath(dataRootArg);

        var env = Environment.GetEnvironmentVariable("PANDO_HOME");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultRootName);
    }

    public static string LobeLibraryDir(string dataRoot) => Path.Combine(dataRoot, "lobe-library");

    /// <summary>Instance directory name: <c>&lt;sanitized-name&gt;-&lt;genesisHash first 8&gt;</c>.</summary>
    public static string InstanceSlug(string name, string genesisHashHex) =>
        $"{Sanitize(name)}-{genesisHashHex[..8]}";

    public static string InstanceDir(string dataRoot, string name, string genesisHashHex) =>
        Path.Combine(dataRoot, InstanceSlug(name, genesisHashHex));

    public static string MemDir(string instanceDir)    => Path.Combine(instanceDir, "mem");
    public static string LobesDir(string instanceDir)   => Path.Combine(instanceDir, "lobes");
    public static string WalletPath(string instanceDir) => Path.Combine(instanceDir, WalletFileName);
    public static string MetaPath(string instanceDir)   => Path.Combine(instanceDir, MetaFileName);

    /// <summary>
    /// Every instance directory under <paramref name="dataRoot"/> that has a readable
    /// <c>identity.meta.json</c>, paired with its parsed meta. Directory scan — no index file (§D3).
    /// </summary>
    public static IEnumerable<(string Dir, IdentityMeta Meta)> EnumerateInstances(string dataRoot)
    {
        if (!Directory.Exists(dataRoot)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(dataRoot))
        {
            var name = Path.GetFileName(dir);
            if (name is "lobe-library" or "bin") continue;

            var meta = IdentityMeta.TryLoad(MetaPath(dir));
            if (meta is not null) yield return (dir, meta);
        }
    }

    /// <summary>Lowercase kebab-case: letters/digits kept, every other run collapses to a single '-'.</summary>
    public static string Sanitize(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var kebab = NonAlnum().Replace(lowered, "-").Trim('-');
        return string.IsNullOrEmpty(kebab) ? "tda" : kebab;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();
}
