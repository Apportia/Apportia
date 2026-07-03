using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpCompress.Archives.Zip;

namespace Apportia.Services;

public sealed class SelfUpdateInfo(Version version, string downloadUrl, string? changelog)
{
    public Version Version { get; } = version;
    public string DownloadUrl { get; } = downloadUrl;
    public string? Changelog { get; } = changelog;
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

public static partial class SelfUpdater
{
    private const string ApiUrl = "https://api.github.com/repos/Apportia/Apportia/releases/latest";

    public static async Task<SelfUpdateInfo?> CheckAsync(CancellationToken ct)
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version;
        if (current == null)
            return null;

        try
        {
            using var client = CreateClient();
            var json = await client.GetStringAsync(ApiUrl, ct);
            var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
            if (release == null || !Version.TryParse(release.TagName, out var latest))
                return null;
            if (latest <= current)
                return null;
            var asset = release.Assets.FirstOrDefault(a => a.BrowserDownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            return asset == null ? null : new SelfUpdateInfo(latest, asset.BrowserDownloadUrl, release.Body);
        }
        catch
        {
            /* network unavailable or API format changed */
            return null;
        }
    }

    public static async Task ApplyAsync(SelfUpdateInfo info, IProgress<int>? progress, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"Apportia-{info.Version}");
        Directory.CreateDirectory(tempDir);

        var zipPath = Path.Combine(tempDir, "update.zip");
        await DownloadAsync(info.DownloadUrl, zipPath, progress, ct);

        var tempRoot = Path.GetFullPath(tempDir + Path.DirectorySeparatorChar);
        using (var archive = ZipArchive.OpenArchive(zipPath))
        {
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                var dest = Path.GetFullPath(Path.Combine(tempDir, entry.Key!.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(tempRoot, StringComparison.Ordinal))
                {
                    Log.Write($"Skipped update entry outside extraction directory: '{entry.Key}'");
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

    private static async Task DownloadAsync(string url, string dest, IProgress<int>? progress, CancellationToken ct)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);

        var buf = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
                progress?.Report((int)(downloaded * 100 / total));
        }
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Apportia", "1.0"));
        return client;
    }
}

[JsonSerializable(typeof(GitHubRelease))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class UpdateJsonContext : JsonSerializerContext;
