using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Apportia.Models;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Apportia.Services;

public static class CustomAppService
{
    public static string CustomAppsDir =>
        Path.Combine(AppContext.BaseDirectory, "CustomApps");

    private static string DataCustomAppsDir =>
        Path.Combine(AppContext.BaseDirectory, "Data", "CustomApps");

    public static string CustomAppImagesDir =>
        Path.Combine(AppContext.BaseDirectory, "Data", "CustomAppImages");

    public static IReadOnlyList<AppEntry> LoadAll()
    {
        var result = new List<AppEntry>();
        var appsDir = CustomAppsDir;
        var dataDir = DataCustomAppsDir;

        if (!Directory.Exists(appsDir))
            return result;

        Directory.CreateDirectory(dataDir);

        CleanOrphanedJsons(appsDir, dataDir);

        foreach (var jsonPath in Directory.GetFiles(dataDir, "*.json"))
        {
            var folderName = Path.GetFileNameWithoutExtension(jsonPath);
            var appDir = Path.Combine(appsDir, folderName);

            try
            {
                var info = JsonSerializer.Deserialize(
                    File.ReadAllText(jsonPath),
                    CustomAppJsonContext.Default.CustomAppInfo);
                if (info is null)
                    continue;

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
                        version = ReadExeVersion(versionExePath);
                        info.PackageVersion = version;
                        info.UpdateDate = actualDate;
                        SaveInfo(jsonPath, info);
                    }
                }

                result.Add(new AppEntry(
                               folderName,
                               info.Name,
                               info.Description,
                               string.IsNullOrWhiteSpace(info.Category) ? "Advanced" : info.Category,
                               info.SubCategory,
                               version,
                               version,
                               string.Empty,
                               string.Empty,
                               info.ExeFile,
                               string.Empty,
                               string.Empty,
                               string.Empty,
                               info.UpdateDate,
                               info.AppUrl
                           ));
            }
            catch
            {
                /* skip corrupt entries – other apps should still load */
            }
        }

        return result;
    }

    public static async Task<string> ImportAppAsync(
        string sourceFolder,
        string exeFile,
        string name,
        string description,
        string website,
        string iconSourcePath,
        string category = "Advanced",
        string subCategory = "",
        string version = "",
        string versionSource = "")
    {
        var baseFolderName = Path.GetFileNameWithoutExtension(exeFile);
        var folderName = baseFolderName;
        while (Directory.Exists(Path.Combine(CustomAppsDir, folderName)))
        {
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLower();
            folderName = baseFolderName + "_" + suffix;
        }

        var destDir = Path.Combine(CustomAppsDir, folderName);

        await Task.Run(() =>
        {
            CopyDirectory(sourceFolder, destDir);
            Directory.CreateDirectory(CustomAppImagesDir);
            SaveIcon(iconSourcePath, Path.Combine(CustomAppImagesDir, folderName + ".png"));
        });

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
            AppUrl = website,
            Category = category,
            SubCategory = subCategory,
            PackageVersion = version,
            VersionSource = versionSource,
            UpdateDate = updateDate
        };
        Directory.CreateDirectory(DataCustomAppsDir);
        await File.WriteAllTextAsync(
            Path.Combine(DataCustomAppsDir, folderName + ".json"),
            JsonSerializer.Serialize(info, CustomAppJsonContext.Default.CustomAppInfo));
        return folderName;
    }

    public static async Task UpdateAppAsync(
        string sectionName,
        string exeFile,
        string name,
        string description,
        string website,
        string? iconSourcePath,
        string category = "Advanced",
        string subCategory = "",
        string version = "",
        string versionSource = "")
    {
        if (!string.IsNullOrEmpty(iconSourcePath))
        {
            Directory.CreateDirectory(CustomAppImagesDir);
            var iconDest = Path.Combine(CustomAppImagesDir, sectionName + ".png");
            await Task.Run(() => SaveIcon(iconSourcePath, iconDest));
        }

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
            AppUrl = website,
            PackageVersion = version,
            VersionSource = versionSource,
            UpdateDate = updateDate
        };
        Directory.CreateDirectory(DataCustomAppsDir);
        await File.WriteAllTextAsync(
            Path.Combine(DataCustomAppsDir, sectionName + ".json"),
            JsonSerializer.Serialize(info, CustomAppJsonContext.Default.CustomAppInfo));
    }

    public static (string Version, string VersionSource) LoadVersionInfo(string sectionName)
    {
        var jsonPath = Path.Combine(DataCustomAppsDir, sectionName + ".json");
        if (!File.Exists(jsonPath))
            return (string.Empty, string.Empty);
        try
        {
            var info = JsonSerializer.Deserialize(
                File.ReadAllText(jsonPath),
                CustomAppJsonContext.Default.CustomAppInfo);
            return (info?.PackageVersion ?? string.Empty, info?.VersionSource ?? string.Empty);
        }
        catch
        {
            /* corrupt json */
            return (string.Empty, string.Empty);
        }
    }

    public static void DeleteData(string sectionName)
    {
        try
        {
            var jsonPath = Path.Combine(DataCustomAppsDir, sectionName + ".json");
            if (File.Exists(jsonPath))
                File.Delete(jsonPath);
            var iconPath = Path.Combine(CustomAppImagesDir, sectionName + ".png");
            if (File.Exists(iconPath))
                File.Delete(iconPath);
        }
        catch
        {
            /* deletion failure must not prevent the uninstall from completing */
        }
    }

    // Delete JSONs in DataCustomAppsDir whose corresponding app folder no longer exists
    private static void CleanOrphanedJsons(string appsDir, string dataDir)
    {
        try
        {
            foreach (var jsonPath in Directory.GetFiles(dataDir, "*.json"))
            {
                var folderName = Path.GetFileNameWithoutExtension(jsonPath);
                if (!Directory.Exists(Path.Combine(appsDir, folderName)))
                    File.Delete(jsonPath);
            }
        }
        catch
        {
            /* cleanup failure is non-fatal */
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

    private static void SaveInfo(string jsonPath, CustomAppInfo info)
    {
        try
        {
            File.WriteAllText(
                jsonPath,
                JsonSerializer.Serialize(info, CustomAppJsonContext.Default.CustomAppInfo));
        }
        catch
        {
            /* version cache save failure must not abort loading */
        }
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

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }
}
