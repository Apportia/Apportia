using System.Diagnostics;

namespace Apportia.Services;

/// Central resolver for the active Wine command and prefix.
/// Two modes:
/// - System: uses ~/.wine and the "wine" on PATH.
/// - Bundled: uses Data/Linux/runners/&lt;version&gt;/bin/wine and Data/Linux/prefixes/default.
public static class WineService
{
    public static readonly string LinuxDir = Path.Combine(AppContext.BaseDirectory, "Data", "Linux");
    public static readonly string RunnersDir = Path.Combine(LinuxDir, "runners");
    public static readonly string PrefixDir = Path.Combine(LinuxDir, "prefixes", "default");

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
        return IsBundled()
            ? PrefixDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");
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
