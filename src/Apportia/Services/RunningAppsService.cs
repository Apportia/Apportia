using System.Diagnostics;
using System.Runtime.Versioning;
using Apportia.Platform;

namespace Apportia.Services;

public static class RunningAppsService
{
    private const int PollMs = 1000;

    private static readonly Lock Gate = new();
    private static HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _timer;
    private static int _pollBusy;

    private static readonly string[] WineOnlyExes =
    [
        "services.exe", "plugplay.exe", "winedevice.exe",
        "rpcss.exe", "wineboot.exe"
    ];

    // An app might ship its own start.exe/explorer.exe/svchost.exe, so match wine's argv shape.
    private static readonly (string Exe, string Arg)[] WineArgSignatures =
    [
        ("start.exe", "/exec"),
        ("explorer.exe", "/desktop"),
        ("svchost.exe", "-k")
    ];

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

    public static List<KillCandidate> GetKillCandidates(string sectionName)
    {
        var bases = CollectBaseDirs();
        if (bases.Count == 0)
            return [];
        if (OperatingSystem.IsLinux())
            return GetKillCandidatesLinux(sectionName, bases);
        if (OperatingSystem.IsWindows())
            return GetKillCandidatesWindows(sectionName, bases);
        return [];
    }

    public static void KillPids(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
            TryKill(pid);
        _ = Task.Run(Poll);
    }

    private static List<KillCandidate> GetKillCandidatesLinux(string sectionName, List<string> bases)
    {
        var result = new List<KillCandidate>();
        var wineBases = bases.Select(b => "Z:" + b.Replace('/', '\\')).ToList();
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(procDir);
            if (name.Length == 0 || !char.IsDigit(name[0]))
                continue;
            if (!int.TryParse(name, out var pid))
                continue;

            try
            {
                var cmdlinePath = Path.Combine(procDir, "cmdline");
                var cmdline = File.Exists(cmdlinePath) ? File.ReadAllText(cmdlinePath) : string.Empty;
                var cwd = ReadLink(Path.Combine(procDir, "cwd"));

                if (!Matches(cmdline, cwd, sectionName, bases, wineBases))
                    continue;
                var comm = ReadComm(procDir);
                if (IsPortableLauncher(cmdline, comm) || IsWineInternal(cmdline))
                    continue;

                var pretty = cmdline.Replace('\0', ' ').TrimEnd();
                result.Add(new KillCandidate(pid, DescribeLinux(cmdline, comm), pretty, TryGetStartTime(pid)));
            }
            catch
            {
                /* /proc entry vanished mid-read */
            }
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static List<KillCandidate> GetKillCandidatesWindows(string sectionName, List<string> bases)
    {
        var result = new List<KillCandidate>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var path = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(path))
                    continue;
                var single = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                TryExtractSection(path, bases, Path.DirectorySeparatorChar, single);
                if (!single.Contains(sectionName))
                    continue;
                if (path.EndsWith("Portable.exe", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("Portable64.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                DateTime? started = null;
                try
                {
                    started = proc.StartTime;
                }
                catch
                {
                    /* access denied */
                }

                var cmd = WindowsCommandLine.TryGet(proc.Id) ?? path;
                result.Add(new KillCandidate(proc.Id, Path.GetFileName(path), cmd, started));
            }
            catch
            {
                /* process exited or access denied */
            }
            finally
            {
                proc.Dispose();
            }
        }

        return result;
    }

    private static string DescribeLinux(string cmdline, string comm)
    {
        foreach (var token in cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            var sep = token.LastIndexOfAny(['/', '\\']);
            return sep < 0 ? token : token[(sep + 1)..];
        }

        return string.IsNullOrEmpty(comm) ? "(unknown)" : comm;
    }

    private static bool Matches(string cmdline, string? cwd, string sectionName, List<string> bases, List<string> wineBases)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryExtractSection(cmdline, bases, '/', found);
        TryExtractSection(cmdline, wineBases, '\\', found);
        if (cwd is not null)
            TryExtractSection(cwd, bases, '/', found);
        return found.Contains(sectionName);
    }

    // Wine helpers inherit cwd from their parent, so path/cwd matching alone catches them.
    private static bool IsWineInternal(string cmdline)
    {
        var tokens = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;
        if (Path.GetFileName(tokens[0]).Equals("wineserver", StringComparison.OrdinalIgnoreCase))
            return true;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var basename = Basename(token);
            var underWindows = token.StartsWith("C:\\windows\\", StringComparison.OrdinalIgnoreCase);

            foreach (var only in WineOnlyExes)
                if (basename.Equals(only, StringComparison.OrdinalIgnoreCase))
                    return true;

            foreach (var (exe, arg) in WineArgSignatures)
            {
                if (!basename.Equals(exe, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (underWindows)
                    return true;
                for (var j = i + 1; j < tokens.Length; j++)
                    if (tokens[j].StartsWith(arg, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }

        return false;

        static string Basename(string token)
        {
            var sep = token.LastIndexOfAny(['/', '\\']);
            return sep < 0 ? token : token[(sep + 1)..];
        }
    }

    private static bool IsPortableLauncher(string cmdline, string comm)
    {
        // comm is truncated to 15 chars; wine sets it to the exe basename minus .exe.
        if (comm.EndsWith("Portable", StringComparison.OrdinalIgnoreCase) ||
            comm.EndsWith("Portable64", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var token in cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = token.LastIndexOfAny(['/', '\\']);
            var basename = sep < 0 ? token : token[(sep + 1)..];
            if (basename.EndsWith("Portable.exe", StringComparison.OrdinalIgnoreCase) ||
                basename.EndsWith("Portable64.exe", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string ReadComm(string procDir)
    {
        try
        {
            return File.ReadAllText(Path.Combine(procDir, "comm")).TrimEnd('\n', '\r');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DateTime? TryGetStartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch
        {
            /* already gone */
        }
    }

    private static void Poll()
    {
        if (Interlocked.Exchange(ref _pollBusy, 1) == 1)
            return;
        try
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
            {
                try
                {
                    Changed?.Invoke(null, section);
                }
                catch
                {
                    /* handler threw – keep polling */
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pollBusy, 0);
        }
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
                /* unresolvable base — skip */
            }
        }
    }

    private static HashSet<string> ScanLinux(List<string> bases)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Wine child argv uses Z:\... for unix paths.
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
                /* /proc entry vanished mid-read */
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
                /* protected process */
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

    public sealed record KillCandidate(int Pid, string Exe, string CommandLine, DateTime? StartTime);
}