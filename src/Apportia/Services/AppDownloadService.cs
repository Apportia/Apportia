using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Apportia.Services;

public sealed class AppDownloadService : IDisposable
{
    private readonly string _downloadDir;
    private readonly HttpClient _http;

    public AppDownloadService(string downloadDir)
    {
        _downloadDir = downloadDir;
        Directory.CreateDirectory(downloadDir);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Wget", "1.21.4"));
    }

    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private static Process? ActiveInstaller { get; set; }

    public void Dispose()
    {
        _http.Dispose();
    }

    public static bool IsWineAvailable()
    {
        if (!IsLinux)
            return true;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("which", "wine")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            /* wine may not be installed – caller checks return value */
            return false;
        }
    }

    /// Converts absolute Linux paths in each arg to Wine Z: drive paths.
    /// Handles bare paths (/foo/bar) and key=value pairs (--file=/foo/bar).
    /// Skips args that already carry a drive letter (Z:\, C:\, etc.).
    public static string[] ConvertArgsForWine(string[] args)
    {
        return !IsLinux ? args : args.Select(ConvertArgForWine).ToArray();
    }

    public async Task<string> DownloadAsync(
        string url,
        string fileName,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var localPath = Path.Combine(_downloadDir, fileName);

        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token, ct);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Connection to {new Uri(url).Host} timed out.");
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(localPath);

            var buffer = new byte[81920];
            long received = 0;
            long lastReported = 0;
            var sw = Stopwatch.StartNew();
            var lastTick = sw.Elapsed;

            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                var now = sw.Elapsed;
                if (!((now - lastTick).TotalSeconds >= 0.25))
                    continue;
                var speed = (received - lastReported) / (now - lastTick).TotalSeconds;
                progress?.Report(new DownloadProgress(received, total, (long)speed));
                lastReported = received;
                lastTick = now;
            }

            progress?.Report(new DownloadProgress(received, total, 0));
        }

        return localPath;
    }

    public static HashResult VerifyHash(string filePath, string expectedHash)
    {
        if (string.IsNullOrEmpty(expectedHash))
            return HashResult.Skipped;

        var bytes = File.ReadAllBytes(filePath);
        var actual = expectedHash.Length switch
        {
            32 => MD5.HashData(bytes),
            40 => SHA1.HashData(bytes),
            64 => SHA256.HashData(bytes),
            96 => SHA384.HashData(bytes),
            128 => SHA512.HashData(bytes),
            _ => []
        };

        if (actual.Length == 0)
            return HashResult.Skipped;

        return Convert.ToHexString(actual).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
            ? HashResult.Valid
            : HashResult.Invalid;
    }

    public static void KillActiveInstaller()
    {
        try
        {
            ActiveInstaller?.Kill(true);
        }
        catch
        {
            /* process may have already exited before the kill request arrived */
        }
    }

    public static async Task ExecuteAsync(
        string installerPath,
        string sectionName,
        string appsBaseDir,
        bool launch = true,
        CancellationToken ct = default)
    {
        var platformExe = Path.Combine(appsBaseDir, "PortableApps.com", "PortableAppsPlatform.exe");
        Directory.CreateDirectory(appsBaseDir);

        // Start platform first (fire and forget – we kill it later)
        Process? platform = null;
        if (File.Exists(platformExe))
        {
            platform = StartProcess(platformExe);
            await Task.Delay(3000, ct);
        }

        // Build DESTINATION: Wine needs Z:\ prefix on Linux
        var dest = IsLinux
            ? "Z:" + appsBaseDir.Replace('/', '\\')
            : appsBaseDir;

        var installerArgs = $"/DESTINATION=\"{dest}\\\\\" /AUTOCLOSE=true /HIDEINSTALLER=true /SILENT=true /SILENTLANGUAGEMODE=always";
        var installer = StartProcess(installerPath, installerArgs);
        ActiveInstaller = installer;

        if (installer is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
            try
            {
                await installer.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    installer.Kill(true);
                }
                catch
                {
                    /* process may have already exited before the kill request arrived */
                }
            }

            ActiveInstaller = null;
            installer.Dispose();
        }

        try
        {
            platform?.Kill();
        }
        catch
        {
            /* platform already exited on its own */
        }

        platform?.Dispose();

        try
        {
            File.Delete(installerPath);
        }
        catch
        {
            /* file may be locked briefly after the installer exits */
        }

        var appExe = Path.Combine(appsBaseDir, sectionName, sectionName + ".exe");
        if (launch && File.Exists(appExe))
            LaunchApp(appExe);
    }

    public static void LaunchApp(string exePath, string? args = null)
    {
        var appDir = Path.GetDirectoryName(exePath)!;
        var sectionName = Path.GetFileNameWithoutExtension(exePath);
        PrepareAppConfig(appDir, sectionName);
        StartProcess(exePath, string.IsNullOrWhiteSpace(args) ? null : args);
    }

    private static string ConvertArgForWine(string arg)
    {
        // key=value: only treat as such when the key part contains no slashes
        var eqIdx = arg.IndexOf('=');
        if (eqIdx <= 0 || arg[..eqIdx].Contains('/'))
            return ConvertPathForWine(arg);
        var key = arg[..eqIdx];
        var value = arg[(eqIdx + 1)..];
        return key + "=" + ConvertPathForWine(value);
    }

    private static string ConvertPathForWine(string path)
    {
        // Already has a drive letter (e.g. Z:\, C:\) – leave as-is
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            return path;

        if (!path.StartsWith('/'))
            return path; // Not an absolute Linux path

        return "Z:" + path.Replace('/', '\\');
    }

    private static void PrepareAppConfig(string appDir, string sectionName)
    {
        var targetIni = Path.Combine(appDir, sectionName + ".ini");
        if (File.Exists(targetIni))
            return;

        var sourceIni = Path.Combine(appDir, "Other", "Source", "AppNamePortable.ini");
        var lines = File.Exists(sourceIni)
            ? File.ReadAllLines(sourceIni).ToList()
            : [];

        SetIniValue(lines, "DisableSplashScreen", "true");
        File.WriteAllLines(targetIni, lines);
    }

    private static void SetIniValue(List<string> lines, string key, string value)
    {
        var keyEntry = $"{key}={value}";

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                continue;
            lines[i] = keyEntry;
            return;
        }

        lines.Add(keyEntry);
    }

    /// Returns the path to a usable 7z binary, or null if none found.
    /// Prefers x64, accepts x86 as fallback. Portable 7-ZipPortable is last resort.
    /// On Linux the portable .exe requires Wine – it is only considered when Wine is available.
    public static string? FindSevenZip(string appsBaseDir)
    {
        IEnumerable<string> paths = IsLinux
            ?
            [
                "/usr/bin/7zz",
                "/usr/local/bin/7zz",
                "/usr/bin/7z",
                "/bin/7z",
                "/usr/local/bin/7z",
                "/usr/bin/7za",
                "/usr/local/bin/7za",
                "/bin/7za"
            ]
            :
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
            ];

        if (IsLinux && !IsWineAvailable())
            return paths.FirstOrDefault(File.Exists);

        var p = Path.Combine(appsBaseDir, "7-ZipPortable", "App");
        paths = paths.Concat([Path.Combine(p, "7-Zip64", "7z.exe"), Path.Combine(p, "7-Zip", "7z.exe")]);

        return paths.FirstOrDefault(File.Exists);
    }

    public static async Task ExtractAsync(
        string sevenZipPath,
        string archivePath,
        string destDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        // Portable .exe on Linux needs Wine; native Linux binaries run directly
        ProcessStartInfo psi;
        if (IsLinux && sevenZipPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var wineArchive = "Z:" + archivePath.Replace('/', '\\');
            var wineDest = "Z:" + destDir.Replace('/', '\\');
            psi = new ProcessStartInfo("wine")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"\"{sevenZipPath}\" x \"{wineArchive}\" -o\"{wineDest}\" -aoa -y"
            };
            var prefix = Environment.GetEnvironmentVariable("WINEPREFIX");
            if (!string.IsNullOrEmpty(prefix))
                psi.Environment["WINEPREFIX"] = prefix;
        }
        else
        {
            psi = new ProcessStartInfo(sevenZipPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"x \"{archivePath}\" -o\"{destDir}\" -aoa -y"
            };
        }

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start 7z process");
        await process.WaitForExitAsync(ct);
    }

    private static Process? StartProcess(string exePath, string? args = null)
    {
        ProcessStartInfo psi;
        if (IsLinux)
        {
            psi = new ProcessStartInfo("wine")
            {
                UseShellExecute = false,
                Arguments = args is not null ? $"\"{exePath}\" {args}" : $"\"{exePath}\""
            };
            var prefix = Environment.GetEnvironmentVariable("WINEPREFIX");
            if (!string.IsNullOrEmpty(prefix))
                psi.Environment["WINEPREFIX"] = prefix;
        }
        else
        {
            psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                Arguments = args ?? string.Empty
            };
        }

        return Process.Start(psi);
    }
}

public enum HashResult
{
    Valid,
    Invalid,
    Skipped
}

public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes, long BytesPerSecond)
{
    public double Percent => TotalBytes > 0 ? (double)BytesReceived / TotalBytes * 100 : -1;

    public string FormatSpeed()
    {
        return BytesPerSecond switch
        {
            >= 1_048_576 => $"{BytesPerSecond / 1_048_576.0:F1} MB/s",
            >= 1_024 => $"{BytesPerSecond / 1_024.0:F0} KB/s",
            _ => $"{BytesPerSecond} B/s"
        };
    }

    public string FormatReceived()
    {
        return TotalBytes > 0
            ? $"{Fmt(BytesReceived)} / {Fmt(TotalBytes)}"
            : Fmt(BytesReceived);

        static string Fmt(long b)
        {
            return b >= 1_048_576 ? $"{b / 1_048_576.0:F1} MB"
                : b >= 1_024 ? $"{b / 1_024.0:F0} KB"
                : $"{b} B";
        }
    }
}
