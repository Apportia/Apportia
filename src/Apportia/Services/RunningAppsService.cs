using System.Diagnostics;

namespace Apportia.Services;

public static class RunningAppsService
{
    private const int PollMs = 1000;

    private static readonly Lock Gate = new();
    private static HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _timer;

    public static event EventHandler<string>? Changed;

    public static void Start()
    {
        lock (Gate)
        {
            _timer ??= new Timer(_ => Poll(), null, 0, PollMs);
        }
    }

    public static bool IsRunning(string sectionName)
    {
        lock (Gate)
        {
            return _running.Contains(sectionName);
        }
    }

    private static void Poll()
    {
        HashSet<string> current;
        try
        {
            current = Scan();
        }
        catch
        {
            return;
        }

        HashSet<string> diff;
        lock (Gate)
        {
            diff = new HashSet<string>(_running, StringComparer.OrdinalIgnoreCase);
            diff.SymmetricExceptWith(current);
            _running = current;
        }

        foreach (var section in diff)
            Changed?.Invoke(null, section);
    }

    private static HashSet<string> Scan()
    {
        var bases = CollectBaseDirs();
        if (bases.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux())
            return ScanLinux(bases);
        if (OperatingSystem.IsWindows())
            return ScanWindows(bases);
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> CollectBaseDirs()
    {
        var list = new List<string>(2);
        TryAdd(AppDeployService.AppsDir);
        TryAdd(CustomAppService.CustomAppsDir);
        return list;

        void TryAdd(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir))
                    list.Add(Path.GetFullPath(dir));
            }
            catch
            {
                // path resolution failed — skip this base
            }
        }
    }

    private static HashSet<string> ScanLinux(List<string> bases)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Wine translates unix paths to Z:\... in child argv; match both forms.
        var wineBases = bases.Select(b => "Z:" + b.Replace('/', '\\')).ToList();

        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(procDir);
            if (name.Length == 0 || !char.IsDigit(name[0]))
                continue;
            if (!int.TryParse(name, out _))
                continue;

            try
            {
                var cmdlinePath = Path.Combine(procDir, "cmdline");
                if (File.Exists(cmdlinePath))
                {
                    var cmdline = File.ReadAllText(cmdlinePath);
                    if (TryExtractSection(cmdline, bases, '/', result))
                        continue;
                    TryExtractSection(cmdline, wineBases, '\\', result);
                }

                var cwd = ReadLink(Path.Combine(procDir, "cwd"));
                if (cwd is not null)
                    TryExtractSection(cwd, bases, '/', result);
            }
            catch
            {
                // Process may have died mid-read or /proc entry is inaccessible.
            }
        }

        return result;
    }

    private static HashSet<string> ScanWindows(List<string> bases)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                    TryExtractSection(path, bases, Path.DirectorySeparatorChar, result);
            }
            catch
            {
                // MainModule access denied for protected processes — skip.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return result;
    }

    private static bool TryExtractSection(string haystack, List<string> bases, char sep, HashSet<string> result)
    {
        foreach (var b in bases)
        {
            var idx = haystack.IndexOf(b, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;
            var start = idx + b.Length;
            if (start >= haystack.Length)
                continue;
            if (haystack[start] == sep)
                start++;
            if (start >= haystack.Length)
                continue;
            var end = haystack.IndexOfAny(['/', '\\', '\0'], start);
            if (end < 0)
                end = haystack.Length;
            if (end > start)
            {
                result.Add(haystack[start..end]);
                return true;
            }
        }

        return false;
    }

    private static string? ReadLink(string linkPath)
    {
        try
        {
            return new FileInfo(linkPath).LinkTarget;
        }
        catch
        {
            return null;
        }
    }
}