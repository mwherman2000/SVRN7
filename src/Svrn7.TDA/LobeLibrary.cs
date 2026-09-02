using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Svrn7.TDA;

/// <summary>
/// Thrown when a LOBE package a TDA needs is not present in the local
/// <c>lobe-library/</c> folder feed. Per docs/AGENTWALLET.md §D6 this is a hard
/// error — the operator must Publish the package there; there is no remote-feed
/// fallback (backlogged).
/// </summary>
public sealed class LobeNotAvailableException(string id, string? version, string libraryDir)
    : Exception($"LOBE package '{id}'{(version is null ? "" : $" {version}")} is not in the LOBE library at " +
                $"'{libraryDir}'. Publish it there (dotnet nuget push <id>.nupkg -s \"{libraryDir}\") and retry.")
{
    public string Id { get; } = id;
    public string? Version { get; } = version;
}

/// <summary>
/// The machine-level LOBE package source: <c>~/.web7-pando/lobe-library/</c>, a
/// plain directory of <c>{Id}.{Version}.nupkg</c> files (docs/AGENTWALLET.md §D6,
/// §D16). A NuGet local folder feed — but a LOBE <c>.nupkg</c> is just a zip with
/// a fixed layout (<c>{Id}.nuspec</c> at the root, files under
/// <c>tools/{Id}/</c>), so this reads it directly without a NuGet client.
/// </summary>
public sealed partial class LobeLibrary
{
    private readonly string _dir;
    private readonly ILogger _log;

    public LobeLibrary(string libraryDir, ILogger log)
    {
        _dir = libraryDir;
        _log = log;
    }

    public string Directory => _dir;

    /// <summary>Every <c>{Id}.{Version}.nupkg</c> in the library, newest-version-first per id.</summary>
    public IReadOnlyList<(string Id, string Version, string NupkgPath)> Available()
    {
        if (!System.IO.Directory.Exists(_dir)) return [];

        var list = new List<(string Id, string Version, string NupkgPath)>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_dir, "*.nupkg"))
        {
            var m = NupkgName().Match(Path.GetFileName(path));
            if (m.Success)
                list.Add((m.Groups["id"].Value, m.Groups["ver"].Value, path));
            else
                _log.LogWarning("LobeLibrary: ignoring '{File}' — not a recognised {{Id}}.{{Version}}.nupkg name.", Path.GetFileName(path));
        }
        return list
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.Version, VersionComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// Resolves a package. When <paramref name="version"/> is given it must match
    /// exactly; otherwise the highest available version of <paramref name="id"/>
    /// is chosen.
    /// </summary>
    /// <exception cref="LobeNotAvailableException">No matching package in the library.</exception>
    public (string Version, string NupkgPath) Resolve(string id, string? version)
    {
        var candidates = Available().Where(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (version is not null)
            candidates = candidates.Where(x => x.Version.Equals(version, StringComparison.OrdinalIgnoreCase)).ToList();

        if (candidates.Count == 0)
            throw new LobeNotAvailableException(id, version, _dir);

        var pick = candidates[0]; // Available() already sorts newest-first per id
        return (pick.Version, pick.NupkgPath);
    }

    // {Id}.{Major.Minor.Patch[-prerelease]}.nupkg  — Id may itself contain dots.
    [GeneratedRegex(@"^(?<id>.+?)\.(?<ver>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\.nupkg$")]
    private static partial Regex NupkgName();

    [GeneratedRegex(@"^(?<id>.+?)\.(?<ver>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$")]
    private static partial Regex IdVersion();

    /// <summary>
    /// Splits <c>"Svrn7.Common.0.8.0"</c> → <c>("Svrn7.Common", "0.8.0")</c>. When
    /// there is no trailing <c>Major.Minor.Patch</c> the whole string is the id and
    /// the version is null. Used to read a LOBE id/version out of an eager-list
    /// path segment or a protocol URI segment.
    /// </summary>
    public static (string Id, string? Version) ParseIdVersion(string idDotVersion)
    {
        var m = IdVersion().Match(idDotVersion);
        return m.Success ? (m.Groups["id"].Value, m.Groups["ver"].Value) : (idDotVersion, null);
    }

    /// <summary>Compares <c>Major.Minor.Patch[-pre]</c> strings; a prerelease sorts below its release.</summary>
    internal sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            static (int maj, int min, int pat, string pre) Parse(string s)
            {
                var dash = s.IndexOf('-');
                var core = dash < 0 ? s : s[..dash];
                var pre  = dash < 0 ? "" : s[(dash + 1)..];
                var p = core.Split('.');
                return (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), pre);
            }

            var pa = Parse(a ?? "0.0.0");
            var pb = Parse(b ?? "0.0.0");
            var c = pa.maj.CompareTo(pb.maj); if (c != 0) return c;
            c = pa.min.CompareTo(pb.min); if (c != 0) return c;
            c = pa.pat.CompareTo(pb.pat); if (c != 0) return c;
            if (pa.pre == pb.pre) return 0;
            if (pa.pre == "") return 1;   // release > prerelease
            if (pb.pre == "") return -1;
            return string.CompareOrdinal(pa.pre, pb.pre);
        }
    }
}
