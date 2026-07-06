using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Apportia.Services;

public sealed record WineRunnerRelease(string Version, string ArchiveName, string DownloadUrl, bool IsStaging);

/// Fetches Kron4ek/Wine-Builds releases (vanilla + staging, 64-bit) and manages
/// download + extract into Data/Linux/runners/&lt;archive-basename&gt;/.
public static partial class WineRunnersClient
{
    private const string ReleasesApi = "https://api.github.com/repos/Kron4ek/Wine-Builds/releases?per_page=30";

    private static readonly HttpClient Http;

    static WineRunnersClient()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Apportia", "1.0"));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// Returns available vanilla + staging 64-bit releases. Newest first; when a version has
    /// both vanilla and staging, staging appears first.
    public static async Task<IReadOnlyList<WineRunnerRelease>> FetchReleasesAsync(CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(ReleasesApi, ct);
        if (JsonSerializer.Deserialize(json, WineReleasesJsonContext.Default.GhReleaseArray) is not { } releases)
            return [];

        return releases
               .SelectMany(r => r.Assets)
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
               .ThenByDescending(r => r.IsStaging)
               .ToList();
    }

    /// Resolves the release named by version, or the newest staging build when "latest".
    public static WineRunnerRelease? PickRelease(IReadOnlyList<WineRunnerRelease> releases, string version)
    {
        if (releases.Count == 0)
            return null;
        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return releases.FirstOrDefault(r => r.IsStaging) ?? releases[0];
        return releases.FirstOrDefault(r => r.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
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
        catch
        {
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
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
                progress?.Report(downloaded / (double)total);
        }
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

    private static string StripExtension(string name)
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

internal sealed class GhRelease
{
    [JsonPropertyName("assets")] public GhAsset[] Assets { get; set; } = [];
}

internal sealed class GhAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}

[JsonSerializable(typeof(GhRelease[]))]
[JsonSerializable(typeof(GhRelease))]
[JsonSerializable(typeof(GhAsset[]))]
[JsonSerializable(typeof(GhAsset))]
internal partial class WineReleasesJsonContext : JsonSerializerContext;
