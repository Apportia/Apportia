using System.Text.Json;
using Apportia.Models;
using Apportia.Text;

namespace Apportia.Services;

public static class CurrentAppService
{
    public static readonly IReadOnlySet<string> ReservedSectionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PortableApps.com", "CommonFiles" };

    private static readonly Lock DbGate = new();
    private static Dictionary<string, CurrentAppInfo>? _cache;
    private static readonly List<string> PendingUnknownDirs = [];

    private static string DatabasePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "current_app_database.json");

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

    public static void Remove(string sectionName)
    {
        lock (DbGate)
        {
            var db = LoadDatabaseUnlocked();
            if (db.Remove(sectionName))
                SaveDatabaseUnlocked(db);
        }
    }

    public static void Register(string sectionName)
    {
        lock (DbGate)
        {
            if (string.IsNullOrEmpty(sectionName) || ReservedSectionNames.Contains(sectionName))
                return;

            var db = LoadDatabaseUnlocked();
            if (!db.TryGetValue(sectionName, out var info))
            {
                info = new CurrentAppInfo();
                db[sectionName] = info;
            }

            var upstream = LoadUpstreamByKey();
            if (upstream.TryGetValue(sectionName, out var entry))
            {
                ApplyReflected(info, entry);
                if (string.IsNullOrEmpty(info.LocalPackageVersion))
                    info.LocalPackageVersion = info.PackageVersion;
                if (string.IsNullOrEmpty(info.LocalDisplayVersion))
                    info.LocalDisplayVersion = info.DisplayVersion;
            }
            else if (string.IsNullOrEmpty(info.Name))
            {
                info.Name = sectionName;
            }

            SaveDatabaseUnlocked(db);
        }
    }

    public static VerifyResult VerifyAgainstDisk()
    {
        lock (DbGate)
        {
            var db = LoadDatabaseUnlocked();
            var appsDir = AppDeployService.AppsDir;
            var structureChanged = false;

            var orphans = db.Keys
                            .Where(section => !EntryIsInstalled(section, appsDir))
                            .ToList();
            foreach (var section in orphans)
                db.Remove(section);
            if (orphans.Count > 0)
                structureChanged = true;

            var pendingBefore = PendingUnknownDirs.Count;
            if (Directory.Exists(appsDir) && File.Exists(AppDatabaseUpdater.CachePath))
            {
                var upstream = LoadUpstreamByKey();
                // Without a loaded upstream DB every installed app would be flagged as unknown
                // and the user would be offered to move them all to CustomApps.
                if (upstream.Count > 0)
                    foreach (var dir in Directory.EnumerateDirectories(appsDir))
                    {
                        var section = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(section) || ReservedSectionNames.Contains(section))
                            continue;
                        if (db.ContainsKey(section))
                            continue;
                        var (exe, _) = AppExecutableService.Resolve(dir, section);
                        if (exe == null)
                            continue;

                        if (upstream.TryGetValue(section, out var entry))
                        {
                            var info = new CurrentAppInfo();
                            ApplyReflected(info, entry);
                            info.LocalPackageVersion = info.PackageVersion;
                            info.LocalDisplayVersion = info.DisplayVersion;
                            db[section] = info;
                            structureChanged = true;
                        }
                        else if (!PendingUnknownDirs.Contains(dir))
                        {
                            PendingUnknownDirs.Add(dir);
                        }
                    }
            }

            if (structureChanged)
                SaveDatabaseUnlocked(db);

            var dates = new Dictionary<string, string>(db.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var section in db.Keys)
            {
                string date;
                if (PluginService.IsPlugin(section))
                {
                    var marker = PluginService.GetMarkerFile(section);
                    date = File.Exists(marker)
                        ? File.GetLastWriteTime(marker).ToString("yyyy-MM-dd")
                        : string.Empty;
                }
                else
                {
                    var appDir = AppDeployService.GetInstallDir(section);
                    var (exe, _) = AppExecutableService.Resolve(appDir, section);
                    date = exe != null
                        ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd")
                        : string.Empty;
                }

                dates[section] = date;
            }

            var structureOrPending = structureChanged || PendingUnknownDirs.Count != pendingBefore;
            return new VerifyResult(structureOrPending, dates);
        }
    }

    private static bool EntryIsInstalled(string section, string appsDir)
    {
        if (PluginService.IsPlugin(section))
            return File.Exists(PluginService.GetMarkerFile(section));
        var dir = Path.Combine(appsDir, section);
        if (!Directory.Exists(dir))
            return false;
        var (exe, _) = AppExecutableService.Resolve(dir, section);
        return exe != null;
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
                var upstream = LoadUpstreamByKey();
                if (upstream.TryGetValue(sectionName, out var entry))
                    ApplyReflected(info, entry);
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            var tmp = DatabasePath + ".tmp";
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(dict, CurrentAppJsonContext.Default.DictionaryStringCurrentAppInfo));
            File.Move(tmp, DatabasePath, true);
            _cache = dict;
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.CurrentApp.SaveDatabaseFailedFormat, ex.Message));
        }
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

    // TODO: drop once users have migrated past the current_app_database.json rollout.
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

            if (hasInstalledDirs && upstream.Count > 0)
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

                var commonFilesDir = PluginService.GetInstallDir();
                if (Directory.Exists(commonFilesDir))
                    foreach (var dir in Directory.EnumerateDirectories(commonFilesDir))
                    {
                        var section = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(section) || !upstream.TryGetValue(section, out var entry))
                            continue;
                        var info = Get(target, section);
                        ApplyReflected(info, entry);
                        info.LocalPackageVersion = info.PackageVersion;
                        info.LocalDisplayVersion = info.DisplayVersion;
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

public sealed record VerifyResult(bool StructureChanged, IReadOnlyDictionary<string, string> CurrentDates);