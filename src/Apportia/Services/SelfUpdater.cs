using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apportia.Text;
using SharpCompress.Archives.Zip;

namespace Apportia.Services;

public sealed class SelfUpdateInfo(Version version, string downloadUrl, string? changelog, string sha256, string assetName)
{
    public Version Version { get; } = version;
    public string DownloadUrl { get; } = downloadUrl;
    public string? Changelog { get; } = changelog;
    public string Sha256 { get; } = sha256;
    public string AssetName { get; } = assetName;
}

public static partial class SelfUpdater
{
    private const string Repo = "Apportia/Apportia";

    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(15);

    private static readonly string StatePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "selfupdate.json");

    // TODO: remove in a future version — migration path from the pre-selfupdate.json marker file
    private static readonly string LegacyMarkerPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "selfupdate_lastcheck");

    public static SelfUpdateInfo? LoadPending()
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version;
        return current == null ? null : ToInfo(LoadState().Pending, current);
    }

    public static async Task<SelfUpdateInfo?> CheckAsync(CancellationToken ct)
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version;
        if (current == null)
            return null;

        var state = LoadState();
        if (state.LastCheckUtc != default && DateTime.UtcNow - state.LastCheckUtc < MinRefreshInterval)
            return ToInfo(state.Pending, current);

        var atom = await GitHubClient.FetchLatestReleaseFromAtomAsync(Repo, ct);
        state.LastCheckUtc = DateTime.UtcNow;

        if (atom != null && Version.TryParse(atom.TagName, out var latest) && latest > current)
        {
            var release = await GitHubClient.FetchLatestReleaseAsync(Repo, ct);
            var asset = release?.Assets.FirstOrDefault(a => a.DownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            state.Pending = asset == null
                ? null
                : new PendingSnapshot
                {
                    Version = latest.ToString(),
                    DownloadUrl = asset.DownloadUrl,
                    Changelog = release?.Body,
                    Sha256 = asset.Sha256Hex,
                    AssetName = asset.Name
                };
        }
        else
        {
            state.Pending = null;
        }

        SaveState(state);
        return ToInfo(state.Pending, current);
    }

    public static async Task ApplyAsync(
        SelfUpdateInfo info,
        IProgress<int>? progress,
        Func<string, string, string, Task<bool>>? onHashMismatch,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"Apportia-{info.Version}");
        Directory.CreateDirectory(tempDir);

        var zipPath = Path.Combine(tempDir, "update.zip");
        await DownloadAsync(info.DownloadUrl, zipPath, progress, ct);

        if (AppDeployService.VerifyHash(zipPath, info.Sha256) == HashResult.Invalid)
        {
            var proceed = onHashMismatch != null && await onHashMismatch(info.AssetName, info.Sha256, zipPath);
            if (!proceed)
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                    // best-effort cleanup; temp dir will be reused on the next attempt
                }

                throw new IOException(string.Format(LogText.Install.HashMismatchFormat, info.AssetName));
            }
        }

        var tempRoot = Path.GetFullPath(tempDir + Path.DirectorySeparatorChar);
        using (var archive = ZipArchive.OpenArchive(zipPath))
        {
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                var dest = Path.GetFullPath(Path.Combine(tempDir, entry.Key!.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(tempRoot, StringComparison.Ordinal))
                {
                    Log.Write(string.Format(LogText.Update.SkippedEntryOutsideExtractionFormat, entry.Key));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using var input = await entry.OpenEntryStreamAsync(ct);
                await using var output = File.Create(dest);
                await input.CopyToAsync(output, ct);
            }
        }

        File.Delete(zipPath);

        var installDir = AppContext.BaseDirectory;

        if (OperatingSystem.IsWindows())
            ApplyWindows(tempDir, installDir, info.Version);
        else if (OperatingSystem.IsLinux())
            ApplyLinux(tempDir, installDir);
    }

    private static SelfUpdateInfo? ToInfo(PendingSnapshot? snapshot, Version current)
    {
        if (snapshot == null
            || string.IsNullOrEmpty(snapshot.Version)
            || !Version.TryParse(snapshot.Version, out var v)
            || v <= current)
            return null;
        return new SelfUpdateInfo(v, snapshot.DownloadUrl, snapshot.Changelog, snapshot.Sha256, snapshot.AssetName);
    }

    private static SelfUpdateState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
                return new SelfUpdateState();
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize(json, SelfUpdateJsonContext.Default.SelfUpdateState) ?? new SelfUpdateState();
        }
        catch
        {
            // corrupt or unreadable state — start fresh so the next check re-populates it
            return new SelfUpdateState();
        }
    }

    private static void SaveState(SelfUpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, SelfUpdateJsonContext.Default.SelfUpdateState));

            // TODO: remove in a future version — cleanup of the pre-selfupdate.json marker file
            if (File.Exists(LegacyMarkerPath))
                File.Delete(LegacyMarkerPath);
        }
        catch
        {
            // state persistence is best-effort; a missed write just means the next start re-checks
        }
    }

    private static async Task DownloadAsync(string url, string dest, IProgress<int>? progress, CancellationToken ct)
    {
        var pct = progress == null ? null : new Progress<double>(p => progress.Report((int)(p * 100)));
        if (!await GitHubClient.DownloadAssetAsync(url, dest, pct, ct))
            throw new IOException(string.Format(LogText.Install.DownloadUrlFailedFormat, url));
    }

    [LibraryImport("libc", EntryPoint = "system", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void System(string command);

    [SupportedOSPlatform("linux")]
    private static void ApplyLinux(string tempDir, string installDir)
    {
        foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(tempDir, file);
            var dest = Path.Combine(installDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest))
                File.Delete(dest);
            File.Copy(file, dest);
        }

        Directory.Delete(tempDir, true);

        var exe = Environment.ProcessPath ?? Path.Combine(installDir, "Apportia");
        File.SetUnixFileMode(
            exe,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var winePrefix = Environment.GetEnvironmentVariable("WINEPREFIX");
        var script = new StringBuilder();
        script.AppendLine("#!/bin/sh");
        script.AppendLine("sleep 2");
        script.AppendLine($"cd \"{installDir}\"");
        if (!string.IsNullOrEmpty(winePrefix))
            script.AppendLine($"export WINEPREFIX=\"{winePrefix}\"");
        script.AppendLine("exec ./Apportia");

        var scriptPath = Path.Combine(Path.GetTempPath(), "apportia-restart.sh");
        File.WriteAllText(scriptPath, script.ToString());
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        System($"sh \"{scriptPath}\" &");
        Environment.Exit(0);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(string tempDir, string installDir, Version version)
    {
        var batPath = Path.Combine(Path.GetTempPath(), $"Apportia-update-{version}.bat");
        var exePath = Path.Combine(installDir, "Apportia.exe");
        File.WriteAllText(batPath, BuildBat(tempDir, installDir, exePath));
        Process.Start(new ProcessStartInfo(batPath)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        Environment.Exit(0);
    }

    private static string BuildBat(string tempDir, string installDir, string exePath)
    {
        return $"""
                @echo off
                cd /D "%~dp0"
                timeout /t 3 /nobreak >nul
                taskkill /f /im Apportia.exe 2>nul
                taskkill /f /im PortableAppsPlatform.exe 2>nul
                timeout /t 2 /nobreak >nul
                xcopy /s /y /e "{tempDir}\*" "{installDir}\"
                rd /s /q "{tempDir}"
                start "" "{exePath}"
                del "%~f0"
                """;
    }
}

internal sealed class SelfUpdateState
{
    public DateTime LastCheckUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingSnapshot? Pending { get; set; }
}

internal sealed class PendingSnapshot
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Changelog { get; set; }

    public string Sha256 { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
}

[JsonSerializable(typeof(SelfUpdateState))]
internal partial class SelfUpdateJsonContext : JsonSerializerContext;
