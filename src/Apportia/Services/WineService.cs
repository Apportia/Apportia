using System.Diagnostics;

namespace Apportia.Services;

/// Central resolver for the active Wine command and prefix.
/// Two modes:
/// - System: uses ~/.wine and the "wine" on PATH.
/// - Bundled: uses Data/Linux/runners/&lt;version&gt;/bin/wine and Data/Linux/prefixes/default.
public static class WineService
{
    public const long FallbackMinFreeBytes = 5L * 1024 * 1024 * 1024;
    public static readonly string FallbackPrefixDir = $"/tmp/apportia.{Environment.UserName}.wine";

    public static readonly string LinuxDir = Path.Combine(AppContext.BaseDirectory, "Data", "Linux");
    public static readonly string RunnersDir = Path.Combine(LinuxDir, "runners");
    public static readonly string DefaultPrefixDir = Path.Combine(LinuxDir, "prefixes", "default");

    private static readonly HashSet<string> CompatibleFsTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext4", "ext3", "ext2", "ext2/ext3", "btrfs", "xfs", "zfs", "f2fs", "tmpfs"
    };

    public static string PrefixDir => SupportsWinePrefix(DefaultPrefixDir) ? DefaultPrefixDir : FallbackPrefixDir;

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

    public static void ApplyEnv(ProcessStartInfo psi)
    {
        if (!OperatingSystem.IsLinux())
            return;
        psi.Environment["WINEPREFIX"] = ResolvePrefix();
        // Prevent winemenubuilder from writing wine-extension-*.desktop into ~/.local/share/applications
        // and from registering MIME associations for the host user.
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
        if (!IsBundled())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");
        var prefix = PrefixDir;
        Directory.CreateDirectory(prefix);
        return prefix;
    }

    private static string? ResolveActiveRunnerDir()
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
