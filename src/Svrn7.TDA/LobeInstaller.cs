using System.IO.Compression;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Svrn7.TDA;

/// <summary>
/// Installs LOBE packages from the machine-level <see cref="LobeLibrary"/> into
/// this instance's own <c>lobes/</c> directory, on first reference
/// (docs/AGENTWALLET.md §D6). A LOBE <c>.nupkg</c> is a zip; the files under its
/// <c>tools/{Id}/</c> folder are extracted flat into
/// <c>&lt;lobesDir&gt;/{Id}.{Version}/</c> — the folder layout the eager list and
/// <c>LobeManager</c>'s descriptor scan expect.
/// </summary>
public sealed class LobeInstaller
{
    private readonly LobeLibrary _library;
    private readonly string _lobesDir;
    private readonly ILogger _log;
    private readonly string? _remoteFeed;
    private HttpClient? _http;

    public LobeInstaller(LobeLibrary library, string lobesDir, ILogger log, string? remoteFeed = null)
    {
        _library = library;
        _lobesDir = lobesDir;
        _log = log;
        _remoteFeed = string.IsNullOrWhiteSpace(remoteFeed) ? null : remoteFeed.TrimEnd('/', '\\');
    }

    /// <summary>The per-instance directory installs land in.</summary>
    public string LobesDir => _lobesDir;

    /// <summary>
    /// Ensures <paramref name="id"/> (optionally pinned to <paramref name="version"/>)
    /// is present under <see cref="LobesDir"/>. No-op when a matching
    /// <c>{id}.*/</c> directory with a <c>.lobe.json</c> already exists. Returns
    /// the install directory.
    /// </summary>
    /// <exception cref="LobeNotAvailableException">The package is not in the library.</exception>
    public string EnsureInstalled(string id, string? version)
    {
        var existing = FindInstalled(id, version);
        if (existing is not null) return existing;

        string resolvedVersion, nupkgPath;
        try
        {
            (resolvedVersion, nupkgPath) = _library.Resolve(id, version);
        }
        catch (LobeNotAvailableException) when (_remoteFeed is not null && version is not null)
        {
            // Not in the local library — try the configured remote feed once,
            // caching the package into lobe-library/ on success (§14 / TDA-006).
            FetchFromRemote(id, version);
            (resolvedVersion, nupkgPath) = _library.Resolve(id, version);
        }

        var destDir = Path.Combine(_lobesDir, $"{id}.{resolvedVersion}");

        _log.LogInformation("LobeInstaller: installing {Id} {Ver} from '{Nupkg}' → '{Dest}'.",
            id, resolvedVersion, Path.GetFileName(nupkgPath), destDir);

        ExtractTools(nupkgPath, destDir);

        if (!Directory.EnumerateFiles(destDir, "*.lobe.json").Any())
            throw new InvalidOperationException(
                $"LOBE package '{Path.GetFileName(nupkgPath)}' produced no .lobe.json under '{destDir}'.");

        return destDir;
    }

    /// <summary>An installed <c>{id}.{version}/</c> (or any <c>{id}.*/</c> when version is null) that has a descriptor, or null.</summary>
    public string? FindInstalled(string id, string? version)
    {
        if (!Directory.Exists(_lobesDir)) return null;

        var pattern = version is null ? $"{id}.*" : $"{id}.{version}";
        foreach (var dir in Directory.EnumerateDirectories(_lobesDir, pattern))
        {
            if (Directory.EnumerateFiles(dir, "*.lobe.json").Any())
                return dir;
        }
        return null;
    }

    /// <summary>
    /// Fetches <c>{id}.{version}.nupkg</c> from <see cref="_remoteFeed"/> into the
    /// LOBE library so the normal local resolve then succeeds. The feed may be a
    /// directory / UNC path, or an HTTP(S) base URL — flat
    /// (<c>{feed}/{id}.{version}.nupkg</c>) or NuGet-v3 flat-container
    /// (<c>{feed}/{id-lower}/{version}/{id-lower}.{version}.nupkg</c>) layout.
    /// </summary>
    private void FetchFromRemote(string id, string version)
    {
        var fileName = $"{id}.{version}.nupkg";
        var dest = Path.Combine(_library.Directory, fileName);
        Directory.CreateDirectory(_library.Directory);

        // Directory / UNC feed.
        if (Directory.Exists(_remoteFeed))
        {
            var src = Directory.EnumerateFiles(_remoteFeed!, fileName, SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new LobeNotAvailableException(id, version, $"{_library.Directory} (remote feed '{_remoteFeed}')");
            File.Copy(src, dest, overwrite: true);
            _log.LogInformation("LobeInstaller: fetched {File} from remote feed '{Feed}'.", fileName, _remoteFeed);
            return;
        }

        // HTTP(S) feed.
        var idLower = id.ToLowerInvariant();
        string[] candidates =
        [
            $"{_remoteFeed}/{fileName}",
            $"{_remoteFeed}/{idLower}/{version}/{idLower}.{version}.nupkg",
        ];

        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        foreach (var url in candidates)
        {
            try
            {
                using var resp = _http.GetAsync(url).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                File.WriteAllBytes(dest, bytes);
                _log.LogInformation("LobeInstaller: fetched {File} from '{Url}'.", fileName, url);
                return;
            }
            catch (HttpRequestException ex)
            {
                _log.LogDebug(ex, "LobeInstaller: remote fetch failed for '{Url}'.", url);
            }
        }

        throw new LobeNotAvailableException(id, version,
            $"{_library.Directory} (also not found on remote feed '{_remoteFeed}')");
    }

    private static void ExtractTools(string nupkgPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var zip = ZipFile.OpenRead(nupkgPath);

        foreach (var entry in zip.Entries)
        {
            // "tools/<pkgId>/<file>" — take only the leaf file name, flatten into destDir.
            var parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[0].Equals("tools", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

            var target = Path.Combine(destDir, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
