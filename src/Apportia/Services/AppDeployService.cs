using System.Diagnostics;
using System.Security.Cryptography;
using Apportia.Text;

namespace Apportia.Services;

public sealed class AppDeployService : IDisposable
{
    private readonly string _downloadDir;
    private readonly HttpClient _http;

    public AppDeployService(string downloadDir)
    {
        _downloadDir = downloadDir;
        Directory.CreateDirectory(downloadDir);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public static string AppsDir =>
        Path.Combine(AppContext.BaseDirectory, "Apps");

    private static Process? ActiveInstaller { get; set; }

    public void Dispose()
    {
        _http.Dispose();
    }

    public static string GetInstallDir(string sectionName)
    {
        return Path.Combine(AppsDir, sectionName);
    }

    public static bool IsWineAvailable()
    {
        return WineService.IsWineReady();
    }

    /// Converts absolute Linux paths in each arg to Wine Z: drive paths.
    /// Handles bare paths (/foo/bar) and key=value pairs (--file=/foo/bar).
    /// Skips args that already carry a drive letter (Z:\, C:\, etc.).
    public static string[] ConvertArgsForWine(string[] args)
    {
        return !OperatingSystem.IsLinux() ? args : args.Select(ConvertArgForWine).ToArray();
    }

    public async Task<string> DownloadAsync(
        string url,
        string fileName,
        IProgress<DownloadProgress>? progress = null,
        string userAgent = "",
        CancellationToken ct = default)
    {
        var localPath = Path.Combine(_downloadDir, fileName);

        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token, ct);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(string.IsNullOrEmpty(userAgent) ? "Wget/1.25" : userAgent);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(string.Format(LogText.Install.ConnectionTimedOutFormat, new Uri(url).Host));
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

        using var stream = File.OpenRead(filePath);
        var actual = expectedHash.Length switch
        {
            32 => MD5.HashData(stream),
            40 => SHA1.HashData(stream),
            64 => SHA256.HashData(stream),
            96 => SHA384.HashData(stream),
            128 => SHA512.HashData(stream),
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

        await WineService.EnsurePrefixReadyAsync(ct);

        // Start platform first (fire and forget – we kill it later)
        Process? platform = null;
        if (File.Exists(platformExe))
        {
            platform = StartProcess(platformExe);
            await Task.Delay(3000, ct);
        }

        // Build DESTINATION: Wine needs Z:\ prefix on Linux
        var dest = OperatingSystem.IsLinux()
            ? "Z:" + appsBaseDir.Replace('/', '\\')
            : appsBaseDir;

        var before = new HashSet<string>(
            Directory.GetFileSystemEntries(appsBaseDir, "*", SearchOption.TopDirectoryOnly),
            StringComparer.OrdinalIgnoreCase);

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

            await WaitForInstallerChildrenAsync(installerPath, ct);
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

        EnforceInstallLocation(appsBaseDir, sectionName, before);

        var appExe = Path.Combine(appsBaseDir, sectionName, sectionName + ".exe");
        if (launch && File.Exists(appExe))
            LaunchApp(appExe);
    }

    private static void EnforceInstallLocation(string appsBaseDir, string sectionName, HashSet<string> before)
    {
        if (!Directory.Exists(appsBaseDir))
            return;

        var newEntries =
            Directory.GetFileSystemEntries(appsBaseDir, "*", SearchOption.TopDirectoryOnly)
                     .Where(e => !before.Contains(e))
                     .ToList();

        if (newEntries.Count == 0)
            return;

        var expectedPath = Path.Combine(appsBaseDir, sectionName);

        // Normal case: installer created exactly one folder – rename it if it doesn't match the section name.
        if (newEntries.Count == 1 && Directory.Exists(newEntries[0]))
        {
            if (!string.Equals(newEntries[0], expectedPath, StringComparison.OrdinalIgnoreCase))
                Directory.Move(newEntries[0], expectedPath);
            return;
        }

        // Fallback: installer scattered multiple entries directly into appsBaseDir – consolidate them.
        Directory.CreateDirectory(expectedPath);
        foreach (var entry in newEntries)
        {
            var name = Path.GetFileName(entry);
            var dest = Path.Combine(expectedPath, name);
            if (Directory.Exists(entry))
                Directory.Move(entry, dest);
            else
                File.Move(entry, dest, true);
        }
    }

    private static async Task WaitForInstallerChildrenAsync(string installerPath, CancellationToken ct)
    {
        var basename = Path.GetFileName(installerPath);
        if (string.IsNullOrEmpty(basename))
            return;
        // Wine argv uses Z:\... for unix paths; also match the raw unix path.
        var wineHaystack = "Z:" + installerPath.Replace('/', '\\');
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        while (!linked.IsCancellationRequested)
        {
            if (!IsInstallerRunning(installerPath, wineHaystack, basename))
                return;
            try
            {
                await Task.Delay(500, linked.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static bool IsInstallerRunning(string installerPath, string wineHaystack, string basename)
    {
        if (OperatingSystem.IsLinux())
            return IsInstallerRunningLinux(installerPath, wineHaystack);
        if (OperatingSystem.IsWindows())
            return IsInstallerRunningWindows(basename);
        return false;
    }

    private static bool IsInstallerRunningLinux(string unixPath, string winePath)
    {
        try
        {
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(procDir);
                if (name.Length == 0 || !char.IsDigit(name[0]) || !int.TryParse(name, out _))
                    continue;
                try
                {
                    var cmdlinePath = Path.Combine(procDir, "cmdline");
                    if (!File.Exists(cmdlinePath))
                        continue;
                    var cmdline = File.ReadAllText(cmdlinePath);
                    if (cmdline.Contains(unixPath, StringComparison.OrdinalIgnoreCase) ||
                        cmdline.Contains(winePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    /* /proc entry vanished mid-read */
                }
            }
        }
        catch
        {
            /* enumeration failed – treat as no running installer */
        }

        return false;
    }

    private static bool IsInstallerRunningWindows(string basename)
    {
        var name = Path.GetFileNameWithoutExtension(basename);
        if (string.IsNullOrEmpty(name))
            return false;
        try
        {
            var procs = Process.GetProcessesByName(name);
            var any = procs.Length > 0;
            foreach (var p in procs)
                p.Dispose();
            return any;
        }
        catch
        {
            return false;
        }
    }

    public static void LaunchApp(string exePath, string? args = null, bool isCustomApp = false)
    {
        var appDir = Path.GetDirectoryName(exePath)!;
        var sectionName = Path.GetFileNameWithoutExtension(exePath);
        if (!isCustomApp)
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

    /// Sets key=value in an ini line list, preserving the key's existing section if present.
    /// If the key is not found, it is inserted in the global (section-less) area above the first section.
    private static void SetIniValue(List<string> lines, string key, string value)
    {
        var keyEntry = $"{key}={value}";
        var keyPrefix = key + "=";

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            lines[i] = keyEntry;
            return;
        }

        var firstSection = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].TrimStart();
            if (!t.StartsWith('[') || !t.TrimEnd().EndsWith(']'))
                continue;
            firstSection = i;
            break;
        }

        if (firstSection < 0)
        {
            lines.Add(keyEntry);
            return;
        }

        var insertAt = firstSection;
        while (insertAt > 0 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
            insertAt--;
        lines.Insert(insertAt, keyEntry);
    }

    /// Returns the path to a usable 7z binary, or null if none found.
    /// Prefers x64, accepts x86 as fallback. Portable 7-ZipPortable is last resort.
    /// On Linux the portable .exe requires Wine – it is only considered when Wine is available.
    public static string? FindSevenZip(string appsBaseDir)
    {
        IEnumerable<string> paths = OperatingSystem.IsLinux()
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

        if (OperatingSystem.IsLinux() && !IsWineAvailable())
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
        if (OperatingSystem.IsLinux() && sevenZipPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var wineArchive = "Z:" + archivePath.Replace('/', '\\');
            var wineDest = "Z:" + destDir.Replace('/', '\\');
            psi = new ProcessStartInfo(WineService.ResolveWineBinary() ?? "wine")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"\"{sevenZipPath}\" x \"{wineArchive}\" -o\"{wineDest}\" -aoa -y"
            };
            WineService.ApplyEnv(psi);
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
                            ?? throw new InvalidOperationException(LogText.Install.SevenZipStartFailed);
        await process.WaitForExitAsync(ct);
    }

    public static void SetIniSectionValue(string filePath, string section, string key, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var lines = File.Exists(filePath) ? File.ReadAllLines(filePath).ToList() : [];
        var header = $"[{section}]";
        var sectionIdx = lines.FindIndex(l =>
                                             l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));

        if (sectionIdx < 0)
        {
            lines.Add(header);
            lines.Add($"{key}={value}");
        }
        else
        {
            var keyPrefix = key + "=";
            var keyIdx = -1;
            for (var i = sectionIdx + 1; i < lines.Count; i++)
            {
                if (lines[i].Trim().StartsWith('['))
                    break;
                if (!lines[i].Trim().StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                keyIdx = i;
                break;
            }

            if (keyIdx >= 0)
                lines[keyIdx] = $"{key}={value}";
            else
                lines.Insert(sectionIdx + 1, $"{key}={value}");
        }

        File.WriteAllLines(filePath, lines);
    }

    /// Returns true when appinfo.ini declares EULAVersion > 0 under [License].
    public static bool ReadEulaVersion(string appInfoPath)
    {
        if (!File.Exists(appInfoPath))
            return false;
        var inLicense = false;
        foreach (var line in File.ReadLines(appInfoPath))
        {
            var t = line.Trim();
            if (t.StartsWith('['))
            {
                inLicense = t.Equals("[License]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inLicense)
                continue;
            var eq = t.IndexOf('=');
            if (eq <= 0)
                continue;
            if (!t[..eq].Trim().Equals("EULAVersion", StringComparison.OrdinalIgnoreCase))
                continue;
            return int.TryParse(t[(eq + 1)..].Trim(), out var v) && v > 0;
        }

        return false;
    }

    private static Process? StartProcess(string exePath, string? args = null)
    {
        var workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;
        ProcessStartInfo psi;
        if (OperatingSystem.IsLinux())
        {
            psi = new ProcessStartInfo(WineService.ResolveWineBinary() ?? "wine")
            {
                UseShellExecute = false,
                WorkingDirectory = workingDir,
                Arguments = args is not null ? $"\"{exePath}\" {args}" : $"\"{exePath}\""
            };
            WineService.ApplyEnv(psi);
        }
        else
        {
            psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDir,
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
        var linux = OperatingSystem.IsLinux();
        return BytesPerSecond switch
        {
            >= 1_048_576 => $"{BytesPerSecond / 1_048_576.0:F1} {(linux ? "MiB" : "MB")}/s",
            >= 1_024 => $"{BytesPerSecond / 1_024.0:F0} {(linux ? "KiB" : "KB")}/s",
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
            var linux = OperatingSystem.IsLinux();
            return b >= 1_048_576 ? $"{b / 1_048_576.0:F1} {(linux ? "MiB" : "MB")}"
                : b >= 1_024 ? $"{b / 1_024.0:F0} {(linux ? "KiB" : "KB")}"
                : $"{b} B";
        }
    }
}