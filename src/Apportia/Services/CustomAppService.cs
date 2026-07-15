using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Apportia.Models;
using Apportia.Text;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Apportia.Services;

public enum ImportMode
{
    Copy,
    Move
}

public sealed record ImportResult(string FolderName, string? SourceDeleteError);

public static class CustomAppService
{
    public const int MinSectionNameLength = 3;
    private static readonly Lock DbGate = new();
    private static Dictionary<string, CustomAppInfo>? _cache;

    private static readonly char[] InvalidSectionChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedSectionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string CustomAppsDir =>
        Path.Combine(AppContext.BaseDirectory, "CustomApps");

    private static string DatabasePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "custom_app_database.json");

    public static string CustomAppImagesDir =>
        Path.Combine(AppContext.BaseDirectory, "Data", "CustomAppImages");

    public static IReadOnlyList<AppEntry> LoadAll()
    {
        var appsDir = CustomAppsDir;
        if (!Directory.Exists(appsDir))
            return [];

        var db = LoadDatabase();
        var dirty = false;
        var result = new List<AppEntry>();

        foreach (var folderName in db.Keys.ToList())
        {
            var info = db[folderName];
            var appDir = Path.Combine(appsDir, folderName);
            if (!Directory.Exists(appDir))
            {
                db.Remove(folderName);
                dirty = true;
                continue;
            }

            var exePath = Path.Combine(appDir, info.ExeFile);
            var versionExePath = string.IsNullOrEmpty(info.VersionSource)
                ? exePath
                : Path.Combine(appDir, info.VersionSource);
            var version = info.PackageVersion;

            if (File.Exists(versionExePath))
            {
                var actualDate = File.GetLastWriteTime(versionExePath).ToString("yyyy-MM-dd");
                if (actualDate != info.UpdateDate || string.IsNullOrEmpty(version))
                {
                    var rawVersion = ReadExeVersion(versionExePath);
                    info.DisplayVersion = rawVersion;
                    info.PackageVersion = NormalizePackageVersion(rawVersion);
                    version = info.PackageVersion;
                    info.UpdateDate = actualDate;
                    dirty = true;
                }
            }

            if (string.IsNullOrEmpty(info.JoinedDate))
            {
                info.JoinedDate = DateTime.Today.ToString("yyyy-MM-dd");
                dirty = true;
            }

            result.Add(new AppEntry(
                           folderName,
                           info.Name,
                           info.Description,
                           info.Website,
                           string.IsNullOrWhiteSpace(info.Category) ? "Advanced" : info.Category,
                           info.SubCategory,
                           info.JoinedDate,
                           string.IsNullOrEmpty(info.DisplayVersion) ? version : info.DisplayVersion,
                           version,
                           info.UpdateDate,
                           info.ExeFile,
                           string.Empty,
                           string.Empty,
                           string.Empty,
                           string.Empty,
                           string.Empty
                       ));
        }

        if (dirty)
            SaveDatabase(db);

        return result;
    }

    public static async Task<ImportResult> ImportAppAsync(
        string sourceFolder,
        string exeFile,
        string name,
        string description,
        string website,
        string iconSourcePath,
        string category = "Advanced",
        string subCategory = "",
        string version = "",
        string versionSource = "",
        string displayVersion = "",
        IProgress<CopyProgress>? copyProgress = null,
        CancellationToken ct = default,
        ImportMode mode = ImportMode.Copy,
        string? preferredFolderName = null)
    {
        Directory.CreateDirectory(CustomAppsDir);

        var isInPlace = IsDirectChildOfCustomApps(sourceFolder);
        var currentName = isInPlace ? Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : null;
        var baseFolderName = string.IsNullOrWhiteSpace(preferredFolderName)
            ? Path.GetFileNameWithoutExtension(exeFile)
            : preferredFolderName.Trim();
        var folderName = baseFolderName;
        while (Directory.Exists(Path.Combine(CustomAppsDir, folderName)) &&
               !(isInPlace && string.Equals(folderName, currentName, StringComparison.OrdinalIgnoreCase)))
        {
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLower();
            folderName = baseFolderName + "_" + suffix;
        }

        var destDir = Path.Combine(CustomAppsDir, folderName);
        string? sourceDeleteError = null;

        try
        {
            if (isInPlace)
            {
                if (!string.Equals(sourceFolder, destDir, StringComparison.OrdinalIgnoreCase))
                    await Task.Run(() => Directory.Move(sourceFolder, destDir), ct);
            }
            else if (mode == ImportMode.Move)
            {
                try
                {
                    await Task.Run(() => Directory.Move(sourceFolder, destDir), ct);
                }
                catch (IOException)
                {
                    // Cross-volume move isn't supported by Directory.Move – copy + delete source.
                    await CopyDirectoryAsync(sourceFolder, destDir, copyProgress, ct);
                    try
                    {
                        await Task.Run(() => Directory.Delete(sourceFolder, true), ct);
                    }
                    catch (Exception ex)
                    {
                        sourceDeleteError = ex.Message;
                    }
                }
            }
            else
            {
                await CopyDirectoryAsync(sourceFolder, destDir, copyProgress, ct);
            }
        }
        catch (OperationCanceledException)
        {
            if (!isInPlace)
            {
                try
                {
                    if (Directory.Exists(destDir))
                        Directory.Delete(destDir, true);
                }
                catch
                {
                    // Best-effort cleanup after user cancel; partial files may remain if in use.
                }
            }

            throw;
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(CustomAppImagesDir);
            SaveIcon(iconSourcePath, Path.Combine(CustomAppImagesDir, folderName + ".png"));
        }, ct);

        var versionExeRelPath = string.IsNullOrEmpty(versionSource) ? exeFile : versionSource;
        var versionExePath = Path.Combine(destDir, versionExeRelPath);
        var updateDate = File.Exists(versionExePath)
            ? File.GetLastWriteTime(versionExePath).ToString("yyyy-MM-dd")
            : string.Empty;

        var info = new CustomAppInfo
        {
            Name = name,
            Description = description,
            ExeFile = exeFile,
            Website = website,
            Category = category,
            SubCategory = subCategory,
            JoinedDate = DateTime.Today.ToString("yyyy-MM-dd"),
            DisplayVersion = displayVersion,
            PackageVersion = version,
            VersionSource = versionSource,
            UpdateDate = updateDate
        };
        UpsertEntry(folderName, info);
        return new ImportResult(folderName, sourceDeleteError);
    }

    public static string ReserveUniqueFolderName(string baseName)
    {
        Directory.CreateDirectory(CustomAppsDir);
        var name = baseName;
        var index = 2;
        while (Directory.Exists(Path.Combine(CustomAppsDir, name)))
            name = baseName + "_" + index++;
        return name;
    }

    public static bool IsDirectChildOfCustomApps(string sourceFolder)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(sourceFolder));
            return parent != null &&
                   string.Equals(Path.TrimEndingDirectorySeparator(parent),
                                 Path.TrimEndingDirectorySeparator(Path.GetFullPath(CustomAppsDir)),
                                 OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static string? ValidateSectionName(string? candidate, string? currentSection = null)
    {
        var name = candidate?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
            return UiText.Dialog.CustomAppEnterSection;
        if (name.Length < MinSectionNameLength)
            return UiText.Dialog.CustomAppSectionTooShort;
        if (name.IndexOfAny(InvalidSectionChars) >= 0)
            return UiText.Dialog.CustomAppInvalidSection;
        foreach (var c in name)
        {
            if (c < 32)
                return UiText.Dialog.CustomAppInvalidSection;
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
            return UiText.Dialog.CustomAppInvalidSection;
        if (ReservedSectionNames.Contains(name))
            return UiText.Dialog.CustomAppInvalidSection;

        if (!string.IsNullOrEmpty(currentSection) &&
            string.Equals(name, currentSection, StringComparison.OrdinalIgnoreCase))
            return null;

        var db = LoadDatabase();
        foreach (var key in db.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return UiText.Dialog.CustomAppSectionExists;
        }

        if (currentSection != null && Directory.Exists(CustomAppsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(CustomAppsDir))
            {
                var folder = Path.GetFileName(dir);
                if (string.Equals(folder, name, StringComparison.OrdinalIgnoreCase))
                    return UiText.Dialog.CustomAppSectionExists;
            }
        }

        return null;
    }

    public static async Task RenameSectionAsync(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return;

        var oldDir = Path.Combine(CustomAppsDir, oldName);
        var newDir = Path.Combine(CustomAppsDir, newName);
        if (Directory.Exists(oldDir))
            await Task.Run(() => Directory.Move(oldDir, newDir));

        var oldIcon = Path.Combine(CustomAppImagesDir, oldName + ".png");
        var newIcon = Path.Combine(CustomAppImagesDir, newName + ".png");
        if (File.Exists(oldIcon))
        {
            Directory.CreateDirectory(CustomAppImagesDir);
            await Task.Run(() => File.Move(oldIcon, newIcon, true));
        }

        lock (DbGate)
        {
            var db = LoadDatabaseUnlocked();
            if (db.Remove(oldName, out var info))
            {
                db[newName] = info;
                SaveDatabaseUnlocked(db);
            }
        }
    }

    public static Task UpdateAppAsync(
        string sectionName,
        string exeFile,
        string name,
        string description,
        string website,
        string? iconSourcePath,
        string category = "Advanced",
        string subCategory = "",
        string version = "",
        string versionSource = "",
        string displayVersion = "")
    {
        if (!string.IsNullOrEmpty(iconSourcePath))
        {
            Directory.CreateDirectory(CustomAppImagesDir);
            var iconDest = Path.Combine(CustomAppImagesDir, sectionName + ".png");
            return Task.Run(() =>
            {
                SaveIcon(iconSourcePath, iconDest);
                WriteUpdatedEntry(sectionName, exeFile, name, description, website, category, subCategory, version, versionSource, displayVersion);
            });
        }

        WriteUpdatedEntry(sectionName, exeFile, name, description, website, category, subCategory, version, versionSource, displayVersion);
        return Task.CompletedTask;
    }

    private static void WriteUpdatedEntry(
        string sectionName,
        string exeFile,
        string name,
        string description,
        string website,
        string category,
        string subCategory,
        string version,
        string versionSource,
        string displayVersion)
    {
        var db = LoadDatabase();
        db.TryGetValue(sectionName, out var existing);
        var joinedDate = existing?.JoinedDate;
        if (string.IsNullOrEmpty(joinedDate))
            joinedDate = DateTime.Today.ToString("yyyy-MM-dd");

        var versionExeRelPath = string.IsNullOrEmpty(versionSource) ? exeFile : versionSource;
        var versionExePath = Path.Combine(CustomAppsDir, sectionName, versionExeRelPath);
        var updateDate = File.Exists(versionExePath)
            ? File.GetLastWriteTime(versionExePath).ToString("yyyy-MM-dd")
            : string.Empty;

        var info = new CustomAppInfo
        {
            Name = name,
            Description = description,
            Category = category,
            SubCategory = subCategory,
            ExeFile = exeFile,
            Website = website,
            JoinedDate = joinedDate,
            DisplayVersion = displayVersion,
            PackageVersion = version,
            VersionSource = versionSource,
            UpdateDate = updateDate
        };
        UpsertEntry(sectionName, info);
    }

    public static (string Version, string VersionSource, string DisplayVersion) LoadVersionInfo(string sectionName)
    {
        var db = LoadDatabase();
        return db.TryGetValue(sectionName, out var info)
            ? (info.PackageVersion, info.VersionSource, info.DisplayVersion)
            : (string.Empty, string.Empty, string.Empty);
    }

    public static void DeleteData(string sectionName)
    {
        try
        {
            lock (DbGate)
            {
                var db = LoadDatabaseUnlocked();
                if (db.Remove(sectionName))
                    SaveDatabaseUnlocked(db);
            }

            var iconPath = Path.Combine(CustomAppImagesDir, sectionName + ".png");
            if (File.Exists(iconPath))
                File.Delete(iconPath);
        }
        catch
        {
            /* deletion failure must not prevent the uninstall from completing */
        }
    }

    private static Dictionary<string, CustomAppInfo> LoadDatabase()
    {
        lock (DbGate)
        {
            return LoadDatabaseUnlocked();
        }
    }

    private static Dictionary<string, CustomAppInfo> LoadDatabaseUnlocked()
    {
        if (_cache is not null)
            return _cache;
        _cache = ReadDatabaseFile();
        MigrateLegacyLayout(_cache);
        return _cache;
    }

    private static Dictionary<string, CustomAppInfo> ReadDatabaseFile()
    {
        try
        {
            if (!File.Exists(DatabasePath))
                return new Dictionary<string, CustomAppInfo>(StringComparer.OrdinalIgnoreCase);
            var dict = JsonSerializer.Deserialize(
                File.ReadAllText(DatabasePath),
                CustomAppJsonContext.Default.DictionaryStringCustomAppInfo);
            return dict is null
                ? new Dictionary<string, CustomAppInfo>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CustomAppInfo>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, CustomAppInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void UpsertEntry(string sectionName, CustomAppInfo info)
    {
        lock (DbGate)
        {
            var db = LoadDatabaseUnlocked();
            db[sectionName] = info;
            SaveDatabaseUnlocked(db);
        }
    }

    private static void SaveDatabase(Dictionary<string, CustomAppInfo> dict)
    {
        lock (DbGate)
        {
            SaveDatabaseUnlocked(dict);
        }
    }

    private static void SaveDatabaseUnlocked(Dictionary<string, CustomAppInfo> dict)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var tmp = DatabasePath + ".tmp";
        File.WriteAllText(
            tmp,
            JsonSerializer.Serialize(dict, CustomAppJsonContext.Default.DictionaryStringCustomAppInfo));
        if (!VerifyRoundTrip(tmp, dict))
        {
            TryDelete(tmp);
            throw new IOException(string.Format(LogText.Custom.DatabaseWriteVerificationFailedFormat, tmp));
        }

        File.Move(tmp, DatabasePath, true);
        _cache = dict;
    }

    private static bool VerifyRoundTrip(string path, Dictionary<string, CustomAppInfo> expected)
    {
        Dictionary<string, CustomAppInfo>? read;
        try
        {
            read = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                CustomAppJsonContext.Default.DictionaryStringCustomAppInfo);
        }
        catch
        {
            return false;
        }

        if (read is null || read.Count != expected.Count)
            return false;

        var wrapped = new Dictionary<string, CustomAppInfo>(read, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, expInfo) in expected)
        {
            if (!wrapped.TryGetValue(key, out var actInfo))
                return false;
            if (!InfosEqual(expInfo, actInfo))
                return false;
        }

        return true;
    }

    private static bool InfosEqual(CustomAppInfo a, CustomAppInfo b)
    {
        return a.Name == b.Name &&
               a.Description == b.Description &&
               a.ExeFile == b.ExeFile &&
               a.Website == b.Website &&
               a.Category == b.Category &&
               a.SubCategory == b.SubCategory &&
               a.JoinedDate == b.JoinedDate &&
               a.DisplayVersion == b.DisplayVersion &&
               a.PackageVersion == b.PackageVersion &&
               a.VersionSource == b.VersionSource &&
               a.UpdateDate == b.UpdateDate;
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

    // TODO: legacy migration – safe to delete this method and its caller in LoadDatabaseUnlocked once
    // all users have upgraded past the version that introduced custom_app_database.json.
    private static void MigrateLegacyLayout(Dictionary<string, CustomAppInfo> target)
    {
        var legacyDir = Path.Combine(AppContext.BaseDirectory, "Data", "CustomApps");
        if (!Directory.Exists(legacyDir))
            return;
        try
        {
            var found = false;
            foreach (var jsonPath in Directory.GetFiles(legacyDir, "*.json"))
            {
                try
                {
                    var info = JsonSerializer.Deserialize(
                        File.ReadAllText(jsonPath),
                        CustomAppJsonContext.Default.CustomAppInfo);
                    if (info is null)
                        continue;
                    var folderName = Path.GetFileNameWithoutExtension(jsonPath);
                    target[folderName] = info;
                    found = true;
                }
                catch
                {
                    /* skip corrupt legacy entry */
                }
            }

            if (found)
                SaveDatabaseUnlocked(target);
            Directory.Delete(legacyDir, true);
        }
        catch
        {
            /* migration failure – leave legacy in place for next run */
        }
    }

    internal static string ReadExeVersion(string exePath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            var version = fvi.ProductVersion?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(version))
                version = fvi.FileVersion?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(version))
                return version;
        }
        catch
        {
            /* fall through to manifest fallback */
        }

        return PeReader.ReadManifestVersion(exePath);
    }

    internal static string NormalizePackageVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var parts = raw.Trim().Split('.');
        var result = new List<string>(4);
        foreach (var part in parts)
        {
            if (result.Count == 4)
                break;
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0)
                break;
            result.Add(digits);
        }

        if (result.Count == 0)
            return string.Empty;
        while (result.Count < 4)
            result.Add("0");
        return string.Join('.', result);
    }

    private static void SaveIcon(string sourcePath, string destPath)
    {
        const int maxSize = 128;
        using var bmp = new Bitmap(sourcePath);
        if (bmp.PixelSize is { Width: <= maxSize, Height: <= maxSize })
        {
            bmp.Save(destPath);
            return;
        }

        using var scaled = bmp.CreateScaledBitmap(new PixelSize(maxSize, maxSize));
        scaled.Save(destPath);
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string dest,
        IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        progress?.Report(new CopyProgress(files.Length, 0, string.Empty));
        Directory.CreateDirectory(dest);
        var done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            await Task.Run(() => File.Copy(file, destFile, true), ct);
            done++;
            progress?.Report(new CopyProgress(files.Length, done, rel));
        }
    }
}