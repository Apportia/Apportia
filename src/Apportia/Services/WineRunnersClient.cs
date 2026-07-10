using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Apportia.Text;

namespace Apportia.Services;

public sealed record WineRunnerRelease(string Version, string ArchiveName, string DownloadUrl, bool IsStaging);

internal sealed class WineReleasesCache
{
    public DateTime FetchedAt { get; set; }
    public List<WineRunnerRelease> Releases { get; set; } = [];
}

/// Fetches Kron4ek/Wine-Builds releases (vanilla + staging, 64-bit) and manages
/// download + extract into Data/Linux/runners/&lt;archive-basename&gt;/.
public static partial class WineRunnersClient
{
    private const string Repo = "Kron4ek/Wine-Builds";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly string CacheFile =
        Path.Combine(AppContext.BaseDirectory, "Data", "wine_releases.json");

    /// Returns available vanilla + staging 64-bit releases. Newest first; when a version has
    /// both vanilla and staging, vanilla appears first (staging tends to break normal apps).
    /// Cached to disk for 6h; falls back to stale cache when the GitHub API is unreachable or
    /// rate-limited.
    public static async Task<IReadOnlyList<WineRunnerRelease>> FetchReleasesAsync(CancellationToken ct = default)
    {
        var cache = LoadCache();
        if (cache != null && DateTime.UtcNow - cache.FetchedAt < CacheTtl)
            return cache.Releases;

        var releases = await GitHubClient.FetchReleasesAsync(Repo, 30, ct);
        if (releases.Count == 0)
            return cache?.Releases ?? [];

        var mapped = releases
                     .SelectMany(r => r.Assets.Count > 0 ? r.Assets : SynthesizeAssets(r.TagName))
                     .Select(a => (Asset: a, Match: AssetNameRegex().Match(a.Name)))
                     .Where(x => x.Match.Success)
                     .Select(x =>
                     {
                         var staging = x.Match.Groups["staging"].Success;
                         var version = x.Match.Groups["ver"].Value + (staging ? "-staging" : string.Empty);
                         return new WineRunnerRelease(version, x.Asset.Name, x.Asset.DownloadUrl, staging);
                     })
                     .GroupBy(r => r.Version)
                     .Select(g => g.First())
                     .OrderByDescending(r => ParseVersion(r.Version))
                     .ThenBy(r => r.IsStaging)
                     .ToList();

        if (mapped.Count == 0)
            return cache?.Releases ?? [];

        SaveCache(new WineReleasesCache { FetchedAt = DateTime.UtcNow, Releases = mapped });
        return mapped;
    }

    /// Atom fallback yields tags without asset lists — reconstruct the two Kron4ek asset
    /// URLs (vanilla and staging) by convention.
    private static IEnumerable<GhAsset> SynthesizeAssets(string tag)
    {
        foreach (var suffix in new[] { "-amd64.tar.xz", "-staging-amd64.tar.xz" })
        {
            var name = $"wine-{tag}{suffix}";
            yield return new GhAsset
            {
                Name = name,
                DownloadUrl = $"https://github.com/{Repo}/releases/download/{tag}/{name}"
            };
        }
    }

    private static WineReleasesCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile))
                return null;
            var text = File.ReadAllText(CacheFile);
            return JsonSerializer.Deserialize(text, WineReleasesJsonContext.Default.WineReleasesCache);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(WineReleasesCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile,
                              JsonSerializer.Serialize(cache, WineReleasesJsonContext.Default.WineReleasesCache));
        }
        catch
        {
            /* best-effort */
        }
    }

    /// Resolves the release named by version, or the newest vanilla build when "latest".
    public static WineRunnerRelease? PickRelease(IReadOnlyList<WineRunnerRelease> releases, string version)
    {
        if (releases.Count == 0)
            return null;
        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return releases.FirstOrDefault(r => !r.IsStaging) ?? releases[0];
        return releases.FirstOrDefault(r => r.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
    }

    /// True when Bundled+"latest" is configured, a runner is installed, and the newest available
    /// release doesn't match the installed one. Returns false on any missing precondition or fetch
    /// failure so background callers can silently skip signalling.
    public static async Task<bool> HasLatestUpdateAsync(CancellationToken ct = default)
    {
        var settings = SettingsService.Load();
        if (!settings.WineMode.Equals("Bundled", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!settings.WineVersion.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return false;
        var installed = WineService.ResolveActiveRunnerDir();
        if (installed is null)
            return false;
        var releases = await FetchReleasesAsync(ct);
        var latest = PickRelease(releases, "latest");
        if (latest is null)
            return false;
        var installedName = Path.GetFileName(installed.TrimEnd(Path.DirectorySeparatorChar));
        var latestName = StripExtension(latest.ArchiveName);
        return !installedName.Equals(latestName, StringComparison.OrdinalIgnoreCase);
    }

    /// Downloads the tarball to a temp file, then extracts into Data/Linux/runners/&lt;basename&gt;/.
    /// Removes older sibling runner directories on success. Returns the runner path or null on failure.
    public static async Task<string?> DownloadAndInstallAsync(
        WineRunnerRelease release,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(WineService.RunnersDir);
        var basename = StripExtension(release.ArchiveName);
        var targetDir = Path.Combine(WineService.RunnersDir, basename);
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
        {
            CleanupOldRunners(basename);
            return targetDir;
        }

        var tempArchive = Path.Combine(WineService.RunnersDir, release.ArchiveName + ".tmp");
        try
        {
            await DownloadWithProgressAsync(release.DownloadUrl, tempArchive, progress, ct);

            Directory.CreateDirectory(targetDir);
            var ok = await ExtractTarballAsync(tempArchive, targetDir, ct);
            if (!ok)
            {
                TryDeleteDir(targetDir);
                return null;
            }

            FlattenSingleTopLevel(targetDir);
            CleanupOldRunners(basename);
            return targetDir;
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Wine.RunnerDownloadFailedFormat, ex.Message));
            TryDeleteDir(targetDir);
            return null;
        }
        finally
        {
            TryDeleteFile(tempArchive);
        }
    }

    private static async Task DownloadWithProgressAsync(
        string url,
        string dest,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (!await GitHubClient.DownloadAssetAsync(url, dest, progress, ct))
            throw new IOException(string.Format(LogText.Install.DownloadUrlFailedFormat, url));
    }

    private static async Task<bool> ExtractTarballAsync(string archive, string destDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("tar")
        {
            ArgumentList = { "-xf", archive, "-C", destDir },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// Kron4ek tarballs contain one top-level directory like "wine-10.0-amd64/". Move its contents up.
    private static void FlattenSingleTopLevel(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
            return;
        var inner = entries[0];
        foreach (var child in Directory.GetFileSystemEntries(inner))
        {
            var target = Path.Combine(dir, Path.GetFileName(child));
            if (Directory.Exists(child))
                Directory.Move(child, target);
            else
                File.Move(child, target);
        }

        Directory.Delete(inner);
    }

    private static void CleanupOldRunners(string keep)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(WineService.RunnersDir))
            {
                if (Path.GetFileName(d).Equals(keep, StringComparison.Ordinal))
                    continue;
                TryDeleteDir(d);
            }
        }
        catch
        {
            /* best-effort */
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            /* leftover; harmless */
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* leftover; harmless */
        }
    }

    public static string StripExtension(string name)
    {
        if (name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            return name[..^".tar.xz".Length];
        return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? name[..^".tar.gz".Length]
            : Path.GetFileNameWithoutExtension(name);
    }

    private static Version ParseVersion(string s)
    {
        var core = s.Split('-')[0];
        return Version.TryParse(core.Contains('.') ? core : core + ".0", out var v) ? v : new Version(0, 0);
    }

    // matches wine-X.Y[.Z][-rc*]-[staging-]amd64.tar.xz  (excludes -wow64, -tkg, -proton, -staging-tkg, etc.)
    [GeneratedRegex(@"^wine-(?<ver>\d+(\.\d+){1,3}(-rc\d+)?)(-(?<staging>staging))?-amd64\.tar\.xz$", RegexOptions.IgnoreCase)]
    private static partial Regex AssetNameRegex();
}

[JsonSerializable(typeof(WineReleasesCache))]
[JsonSerializable(typeof(WineRunnerRelease))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class WineReleasesJsonContext : JsonSerializerContext;