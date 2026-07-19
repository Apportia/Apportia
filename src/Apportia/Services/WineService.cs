using System.Diagnostics;
using System.Text.RegularExpressions;
using Apportia.Text;

namespace Apportia.Services;

// Central resolver for the active Wine command and prefix. In System mode the "wine" on PATH
// and ~/.wine are used; in Bundled mode Data/Linux/runners/<version>/bin/wine and
// Data/Linux/prefixes/default.
public static class WineService
{
    public const long FallbackMinFreeBytes = 5L * 1024 * 1024 * 1024;
    public static readonly string FallbackPrefixDir = $"/tmp/apportia.{Environment.UserName}.wine";

    public static readonly string LinuxDir = Path.Combine(AppContext.BaseDirectory, "Data", "Linux");
    public static readonly string RunnersDir = Path.Combine(LinuxDir, "runners");
    public static readonly string FontsDir = Path.Combine(LinuxDir, "fonts");
    public static readonly string DefaultPrefixDir = Path.Combine(LinuxDir, "prefixes", "default");

    private static readonly HashSet<string> CompatibleFsTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext4", "ext3", "ext2", "ext2/ext3", "btrfs", "xfs", "zfs", "f2fs", "tmpfs"
    };

    private static readonly Regex SharedObjectRegex = new(
        @"/[\w./+\-]*\.so(?:\.\d+)*",
        RegexOptions.Compiled);

    public static string PrefixDir =>
        !IsBundled()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine")
            : SupportsWinePrefix(DefaultPrefixDir)
                ? DefaultPrefixDir
                : FallbackPrefixDir;

    public static bool SupportsWinePrefix(string dir)
    {
        var fsType = GetFilesystemType(dir);
        return fsType != null && CompatibleFsTypes.Contains(fsType);
    }

    private static string? Probe(string command, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetFilesystemType(string dir)
    {
        try
        {
            var probe = dir;
            while (!Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == probe)
                    return null;
                probe = parent;
            }

            return Probe("findmnt", ["-n", "-o", "FSTYPE", "-T", probe])
                   ?? Probe("stat", ["-f", "-c", "%T", probe]);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsSystemWineAvailable()
    {
        if (!OperatingSystem.IsLinux())
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
            return false;
        }
    }

    public static bool IsWineReady()
    {
        return ResolveWineBinary() is not null;
    }

    public static string? ResolveWineBinary()
    {
        if (!OperatingSystem.IsLinux())
            return "wine";

        if (!IsBundled())
            return IsSystemWineAvailable() ? "wine" : null;

        var runner = ResolveActiveRunnerDir();
        if (runner is null)
            return null;
        var bin = Path.Combine(runner, "bin", "wine");
        return File.Exists(bin) ? bin : null;
    }

    public static async Task EnsurePrefixReadyAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var wine = ResolveWineBinary()
                   ?? throw new InvalidOperationException(LogText.Wine.WineNotAvailable);
        var prefix = ResolvePrefix();

        if (IsPrefixValid(prefix))
            return;

        Directory.CreateDirectory(prefix);
        // Seed fonts before wineboot so its font-registry pass picks them up.
        if (SettingsService.Load().WineInstallFonts)
            WinePrefixFonts.Apply();
        var psi = new ProcessStartInfo(wine)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("wineboot");
        psi.ArgumentList.Add("-u");
        ApplyEnv(psi);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException(
                             string.Format(LogText.Wine.LaunchFailedFormat, wine, prefix));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(true);
            }
            catch
            {
                /* process may have already exited before the kill request */
            }

            throw new InvalidOperationException(
                string.Format(LogText.Wine.PrefixInitTimedOutFormat, prefix));
        }

        // wineserver flushes user.reg/system.reg asynchronously after wineboot exits.
        for (var i = 0; i < 100 && !IsPrefixValid(prefix); i++)
            await Task.Delay(100, ct);

        if (IsPrefixValid(prefix))
            return;

        var stderr = string.Empty;
        var stdout = string.Empty;
        try
        {
            stderr = await stderrTask;
        }
        catch
        {
            /* pipe closed or read cancelled – diagnose with whatever we have */
        }

        try
        {
            stdout = await stdoutTask;
        }
        catch
        {
            /* pipe closed or read cancelled – diagnose with whatever we have */
        }

        var combined = stderr + "\n" + stdout;

        throw new InvalidOperationException(DiagnosePrefixFailure(prefix, combined));
    }

    private static string DiagnosePrefixFailure(string prefix, string wineOutput)
    {
        var missing = ExtractMissingSharedObjects(wineOutput);
        if (missing.Count > 0)
        {
            var list = string.Join('\n', missing.Select(m => "• " + m));
            return LogText.Wine.PrefixMissingSharedLibrariesHeader + "\n\n" + list;
        }

        return string.Format(LogText.Wine.PrefixInitUnknownFailureFormat, prefix);
    }

    private static List<string> ExtractMissingSharedObjects(string wineOutput)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match m in SharedObjectRegex.Matches(wineOutput))
            if (seen.Add(m.Value))
                result.Add(m.Value);
        return result;
    }

    public static bool IsPrefixReady()
    {
        return OperatingSystem.IsLinux() && IsPrefixValid(PrefixDir);
    }

    private static bool IsPrefixValid(string prefix)
    {
        return File.Exists(Path.Combine(prefix, "system.reg"))
               && File.Exists(Path.Combine(prefix, "user.reg"))
               && Directory.Exists(Path.Combine(prefix, "drive_c", "windows", "system32"));
    }

    public static void ApplyEnv(ProcessStartInfo psi)
    {
        if (!OperatingSystem.IsLinux())
            return;
        psi.Environment["WINEPREFIX"] = ResolvePrefix();
        // Prevent winemenubuilder from writing wine-extension-*.desktop
        // into ~/.local/share/applications and from registering MIME
        // associations for the host user.
        psi.Environment["WINEDLLOVERRIDES"] = "winemenubuilder.exe=";
        if (!IsBundled())
            return;
        var runner = ResolveActiveRunnerDir();
        if (runner is null)
            return;
        var binDir = Path.Combine(runner, "bin");
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        psi.Environment["PATH"] = binDir + Path.PathSeparator + existingPath;
    }

    private static bool IsBundled()
    {
        return SettingsService.Load().WineMode.Equals("Bundled", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePrefix()
    {
        var prefix = PrefixDir;
        Directory.CreateDirectory(prefix);
        return prefix;
    }

    public static string? ResolveActiveRunnerDir()
    {
        if (!Directory.Exists(RunnersDir))
            return null;
        return new DirectoryInfo(RunnersDir)
               .EnumerateDirectories()
               .OrderByDescending(d => d.LastWriteTimeUtc)
               .Select(d => d.FullName)
               .FirstOrDefault();
    }
}