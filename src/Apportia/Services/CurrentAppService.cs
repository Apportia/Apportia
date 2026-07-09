using System.Text.Json;
using Apportia.Models;

namespace Apportia.Services;

public static class CurrentAppService
{
    // Reserved section names that must never appear as a registered installed app.
    public static readonly IReadOnlySet<string> ReservedSectionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PortableApps.com" };

    private static readonly Lock DbGate = new();
    private static Dictionary<string, CurrentAppInfo>? _cache;
    private static readonly List<string> PendingUnknownDirs = [];

    private static string DatabasePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "current_app_database.json");

    // Directories found under Apps/ during migration that couldn't be matched to app_database.json.
    // The UI is expected to prompt the user to either move each to CustomApps or delete it.
    public static IReadOnlyList<string> ConsumePendingUnknownDirs()
    {
        lock (DbGate)
        {
            var copy = PendingUnknownDirs.ToArray();
            PendingUnknownDirs.Clear();
            return copy;
        }
    }

    public static IReadOnlyDictionary<string, CurrentAppInfo> LoadAll()
    {
        return LoadDatabase();
    }

    public static bool TryGet(string sectionName, out CurrentAppInfo info)
    {
        var db = LoadDatabase();
        return db.TryGetValue(sectionName, out info!);
    }

    public static string GetExeFile(string sectionName)
    {
        var db = LoadDatabase();
        return db.TryGetValue(sectionName, out var info) ? info.ExeFile : string.Empty;
    }

    public static (string DisplayVersion, string PackageVersion) GetLocalVersion(string sectionName)
    {
        var db = LoadDatabase();
        return db.TryGetValue(sectionName, out var info)
            ? (info.LocalDisplayVersion, info.LocalPackageVersion)
            : (string.Empty, string.Empty);
    }

    public static void SetExeFile(string sectionName, string exeFile)
    {
        MutateEntry(sectionName, info => info.ExeFile = exeFile);
    }

    public static void SetLocalVersion(string sectionName, string displayVersion, string packageVersion)
    {
        MutateEntry(sectionName, info =>
        {
            info.LocalDisplayVersion = displayVersion;
            info.LocalPackageVersion = packageVersion;
        });
    }

    public static void Upsert(string sectionName, CurrentAppInfo info)
    {
        lock (DbGate)
        {
            var db = LoadDatabaseUnlocked();
            db[sectionName] = info;
            SaveDatabaseUnlocked(db);
        }
    }

    public static void Remove(string sectionName)
    {
        lock (DbGate)
        {
            var db = LoadDatabaseUnlocked();
            if (db.Remove(sectionName))
                SaveDatabaseUnlocked(db);
        }
    }

    // Bulk replacement — used by background verify to prune orphans and add newly discovered apps in one write.
    public static void ReplaceAll(Dictionary<string, CurrentAppInfo> snapshot)
    {
        lock (DbGate)
        {
            var db = new Dictionary<string, CurrentAppInfo>(snapshot, StringComparer.OrdinalIgnoreCase);
            SaveDatabaseUnlocked(db);
        }
    }

    private static void MutateEntry(string sectionName, Action<CurrentAppInfo> mutator)
    {
        lock (DbGate)
        {
            var db = LoadDatabaseUnlocked();
            if (!db.TryGetValue(sectionName, out var info))
            {
                info = new CurrentAppInfo();
                db[sectionName] = info;
            }

            mutator(info);
            SaveDatabaseUnlocked(db);
        }
    }

    private static Dictionary<string, CurrentAppInfo> LoadDatabase()
    {
        lock (DbGate)
        {
            return LoadDatabaseUnlocked();
        }
    }

    private static Dictionary<string, CurrentAppInfo> LoadDatabaseUnlocked()
    {
        if (_cache is not null)
            return _cache;
        _cache = ReadDatabaseFile();
        MigrateLegacyLayout(_cache);
        return _cache;
    }

    private static Dictionary<string, CurrentAppInfo> ReadDatabaseFile()
    {
        try
        {
            if (!File.Exists(DatabasePath))
                return new Dictionary<string, CurrentAppInfo>(StringComparer.OrdinalIgnoreCase);
            var dict = JsonSerializer.Deserialize(
                File.ReadAllText(DatabasePath),
                CurrentAppJsonContext.Default.DictionaryStringCurrentAppInfo);
            return dict is null
                ? new Dictionary<string, CurrentAppInfo>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CurrentAppInfo>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, CurrentAppInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveDatabaseUnlocked(Dictionary<string, CurrentAppInfo> dict)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var tmp = DatabasePath + ".tmp";
        File.WriteAllText(
            tmp,
            JsonSerializer.Serialize(dict, CurrentAppJsonContext.Default.DictionaryStringCurrentAppInfo));
        if (!VerifyRoundTrip(tmp, dict))
        {
            TryDelete(tmp);
            throw new IOException($"Current app database write verification failed: {tmp}");
        }

        File.Move(tmp, DatabasePath, true);
        _cache = dict;
    }

    private static bool VerifyRoundTrip(string path, Dictionary<string, CurrentAppInfo> expected)
    {
        Dictionary<string, CurrentAppInfo>? read;
        try
        {
            read = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                CurrentAppJsonContext.Default.DictionaryStringCurrentAppInfo);
        }
        catch
        {
            return false;
        }

        if (read is null || read.Count != expected.Count)
            return false;

        var wrapped = new Dictionary<string, CurrentAppInfo>(read, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, exp) in expected)
        {
            if (!wrapped.TryGetValue(key, out var act))
                return false;
            if (!InfosEqual(exp, act))
                return false;
        }

        return true;
    }

    private static bool InfosEqual(CurrentAppInfo a, CurrentAppInfo b)
    {
        return a.LocalPackageVersion == b.LocalPackageVersion &&
               a.LocalDisplayVersion == b.LocalDisplayVersion &&
               a.ExeFile == b.ExeFile &&
               a.Name == b.Name &&
               a.Description == b.Description &&
               a.Website == b.Website &&
               a.Category == b.Category &&
               a.SubCategory == b.SubCategory &&
               a.DisplayVersion == b.DisplayVersion &&
               a.PackageVersion == b.PackageVersion &&
               a.UpdateDate == b.UpdateDate &&
               a.DownloadFile == b.DownloadFile &&
               a.JoinedDate == b.JoinedDate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* best effort */
        }
    }

    // WIP: legacy migration – safe to delete this method and its caller in LoadDatabaseUnlocked once
    // all users have upgraded past the version that introduced current_app_database.json.
    private static void MigrateLegacyLayout(Dictionary<string, CurrentAppInfo> target)
    {
        if (target.Count > 0)
            return;

        var executablesPath = Path.Combine(AppContext.BaseDirectory, "Data", "executables.json");
        var versionsPath = Path.Combine(AppContext.BaseDirectory, "Data", "local_app_versions.json");
        var appsDir = AppDeployService.AppsDir;
        var hasLegacyFiles = File.Exists(executablesPath) || File.Exists(versionsPath);
        var hasInstalledDirs = Directory.Exists(appsDir) && Directory.EnumerateDirectories(appsDir).Any();

        if (!hasLegacyFiles && !hasInstalledDirs)
            return;

        try
        {
            var upstream = LoadUpstreamByKey();

            if (hasInstalledDirs)
            {
                foreach (var dir in Directory.EnumerateDirectories(appsDir))
                {
                    var section = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(section) || ReservedSectionNames.Contains(section))
                        continue;
                    if (!upstream.TryGetValue(section, out var entry))
                    {
                        PendingUnknownDirs.Add(dir);
                        continue;
                    }

                    var info = Get(target, section);
                    ApplyReflected(info, entry);
                }
            }

            if (File.Exists(executablesPath))
            {
                try
                {
                    var exeMap = JsonSerializer.Deserialize(
                        File.ReadAllText(executablesPath),
                        ExecutablesJsonContext.Default.DictionaryStringString);
                    if (exeMap is not null)
                    {
                        foreach (var (section, exe) in exeMap)
                        {
                            if (ReservedSectionNames.Contains(section) || !upstream.ContainsKey(section))
                                continue;
                            var info = Get(target, section);
                            info.ExeFile = exe;
                            ApplyReflected(info, upstream[section]);
                        }
                    }
                }
                catch
                {
                    /* corrupt legacy exe file – ignore */
                }
            }

            if (File.Exists(versionsPath))
            {
                try
                {
                    var versionMap = JsonSerializer.Deserialize(
                        File.ReadAllText(versionsPath),
                        LocalVersionJsonContext.Default.DictionaryStringLocalAppVersion);
                    if (versionMap is not null)
                    {
                        foreach (var (section, ver) in versionMap)
                        {
                            if (ReservedSectionNames.Contains(section) || !upstream.ContainsKey(section))
                                continue;
                            var info = Get(target, section);
                            info.LocalDisplayVersion = ver.DisplayVersion;
                            info.LocalPackageVersion = ver.PackageVersion;
                            ApplyReflected(info, upstream[section]);
                        }
                    }
                }
                catch
                {
                    /* corrupt legacy version file – ignore */
                }
            }

            // Fallback for entries where the legacy version file didn't cover the install:
            // seed local version from the upstream latest so LocalPackageVersion / LocalDisplayVersion are never empty.
            foreach (var info in target.Values)
            {
                if (string.IsNullOrEmpty(info.LocalPackageVersion))
                    info.LocalPackageVersion = info.PackageVersion;
                if (string.IsNullOrEmpty(info.LocalDisplayVersion))
                    info.LocalDisplayVersion = info.DisplayVersion;
            }

            if (target.Count > 0)
                SaveDatabaseUnlocked(target);

            TryDelete(executablesPath);
            TryDelete(versionsPath);
        }
        catch
        {
            /* migration failure – leave legacy files in place for next run */
        }

        return;

        static CurrentAppInfo Get(Dictionary<string, CurrentAppInfo> dict, string section)
        {
            if (dict.TryGetValue(section, out var existing))
                return existing;
            var fresh = new CurrentAppInfo();
            dict[section] = fresh;
            return fresh;
        }
    }

    private static Dictionary<string, AppEntry> LoadUpstreamByKey()
    {
        var cachePath = AppDatabaseUpdater.CachePath;
        if (!File.Exists(cachePath))
            return new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return AppDatabaseParser.ParseJson(cachePath)
                                    .GroupBy(e => e.SectionName, StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void ApplyReflected(CurrentAppInfo info, AppEntry upstream)
    {
        info.Name = upstream.Name;
        info.Description = upstream.Description;
        info.Website = upstream.Website;
        info.Category = upstream.Category;
        info.SubCategory = upstream.SubCategory;
        info.DisplayVersion = upstream.DisplayVersion;
        info.PackageVersion = upstream.PackageVersion;
        info.UpdateDate = upstream.UpdateDate;
        info.DownloadFile = upstream.DownloadFile;
        info.JoinedDate = upstream.JoinedDate;
    }
}
