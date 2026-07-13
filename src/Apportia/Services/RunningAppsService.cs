using System.Diagnostics;
using System.Runtime.Versioning;
using Apportia.Platform;

namespace Apportia.Services;

public static class RunningAppsService
{
    private const int PollMs = 1000;

    private static readonly Lock Gate = new();
    private static readonly Lock ExeCacheGate = new();
    private static HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _timer;
    private static int _pollBusy;

    private static readonly Dictionary<string, Dictionary<string, bool>> ExePresenceBySection =
        new(StringComparer.OrdinalIgnoreCase);

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
        return OperatingSystem.IsWindows() ? GetKillCandidatesWindows(sectionName, bases) : [];
    }

    public static void KillPids(IEnumerable<int> pids)
    {
        var list = pids.Distinct().ToList();
        if (list.Count == 0)
            return;
        foreach (var pid in SortLeafFirst(list))
            TryKill(pid);
        _ = Task.Run(Poll);
    }

    private static List<int> SortLeafFirst(List<int> pids)
    {
        var parentInSet = new Dictionary<int, int>();
        var pidSet = new HashSet<int>(pids);
        foreach (var pid in pids)
        {
            var ppid = TryGetParentPid(pid);
            if (ppid is { } p && pidSet.Contains(p) && p != pid)
                parentInSet[pid] = p;
        }

        var childCount = pids.ToDictionary(p => p, _ => 0);
        foreach (var parent in parentInSet.Values)
            childCount[parent]++;

        var result = new List<int>(pids.Count);
        var queue = new Queue<int>();
        foreach (var (pid, cnt) in childCount)
            if (cnt == 0)
                queue.Enqueue(pid);
        while (queue.TryDequeue(out var pid))
        {
            result.Add(pid);
            if (!parentInSet.TryGetValue(pid, out var parent))
                continue;
            if (--childCount[parent] == 0)
                queue.Enqueue(parent);
        }

        foreach (var pid in pids.Where(pid => !result.Contains(pid)))
            result.Add(pid);
        return result;
    }

    private static int? TryGetParentPid(int pid)
    {
        if (OperatingSystem.IsLinux())
            return TryGetParentPidLinux(pid);
        return OperatingSystem.IsWindows() ? Win32Process.TryGetParentPid(pid) : null;
    }

    private static int? TryGetParentPidLinux(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("PPid:", StringComparison.Ordinal))
                    continue;
                return int.TryParse(line.AsSpan(5).Trim(), out var ppid) ? ppid : null;
            }
        }
        catch
        {
            // /proc entry vanished or unreadable
        }

        return null;
    }

    public static void InvalidateExeCache(string? sectionName = null)
    {
        lock (ExeCacheGate)
        {
            if (sectionName is null)
                ExePresenceBySection.Clear();
            else
                ExePresenceBySection.Remove(sectionName);
        }
    }

    private static bool SectionContainsExe(string sectionName, string exeName, List<string> bases)
    {
        if (string.IsNullOrEmpty(exeName))
            return false;

        lock (ExeCacheGate)
        {
            if (!ExePresenceBySection.TryGetValue(sectionName, out var map))
            {
                map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                ExePresenceBySection[sectionName] = map;
            }

            if (map.TryGetValue(exeName, out var cached))
                return cached;

            var found = false;
            foreach (var sectionDir in bases.Select(b => Path.Combine(b, sectionName)).Where(Directory.Exists))
            {
                try
                {
                    using var e = Directory.EnumerateFiles(sectionDir, exeName, SearchOption.AllDirectories).GetEnumerator();
                    if (!e.MoveNext())
                        continue;
                    found = true;
                    break;
                }
                catch
                {
                    /* enumeration raced with uninstall */
                }
            }

            map[exeName] = found;
            return found;
        }
    }

    private static string GetProcessExeName(string cmdline, string comm)
    {
        foreach (var token in cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            var sep = token.LastIndexOfAny(['/', '\\']);
            return sep < 0 ? token : token[(sep + 1)..];
        }

        var argv0 = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(argv0))
            return comm;
        var s = argv0.LastIndexOfAny(['/', '\\']);
        return s < 0 ? argv0 : argv0[(s + 1)..];
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
                if (!SectionContainsExe(sectionName, GetProcessExeName(cmdline, comm), bases))
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

            if (WineOnlyExes.Any(s => basename.Equals(s, StringComparison.OrdinalIgnoreCase)))
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
        return cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                      .Select(t => t[(t.LastIndexOfAny(['/', '\\']) + 1)..])
                      .Any(b => b.EndsWith("Portable.exe", StringComparison.OrdinalIgnoreCase) ||
                                b.EndsWith("Portable64.exe", StringComparison.OrdinalIgnoreCase));
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
        return OperatingSystem.IsWindows() ? ScanWindows(bases) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                var cmdline = File.Exists(cmdlinePath) ? File.ReadAllText(cmdlinePath) : string.Empty;
                var cwd = ReadLink(Path.Combine(procDir, "cwd"));

                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                TryExtractSection(cmdline, bases, '/', candidates);
                TryExtractSection(cmdline, wineBases, '\\', candidates);
                if (cwd is not null)
                    TryExtractSection(cwd, bases, '/', candidates);

                if (candidates.Count == 0)
                    continue;

                var comm = ReadComm(procDir);
                var exeName = GetProcessExeName(cmdline, comm);

                foreach (var section in candidates.Where(section => SectionContainsExe(section, exeName, bases)))
                    result.Add(section);
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

    private static void TryExtractSection(string haystack, List<string> bases, char sep, HashSet<string> result)
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
            if (end <= start)
                continue;
            result.Add(haystack[start..end]);
            return;
        }
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
