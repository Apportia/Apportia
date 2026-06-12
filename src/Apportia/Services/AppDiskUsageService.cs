using System.Runtime.CompilerServices;
using System.Text.Json;
using Apportia.Models;

namespace Apportia.Services;

public static class AppDiskUsageService
{
    private static readonly string CachePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "disk_usage.json");

    public static DiskUsageCache LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize(
                    File.ReadAllText(CachePath),
                    DiskUsageCacheJsonContext.Default.DiskUsageCache) ?? new DiskUsageCache();
        }
        catch
        {
            /* corrupt cache – start fresh */
        }

        return new DiskUsageCache();
    }

    public static void SaveCache(DiskUsageCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(
                CachePath,
                JsonSerializer.Serialize(cache, DiskUsageCacheJsonContext.Default.DiskUsageCache));
        }
        catch
        {
            /* cache save failure must not affect the user */
        }
    }

    public static string FormatBytes(long bytes)
    {
        return bytes >= 1_073_741_824L ? $"{bytes / 1_073_741_824.0:F1} GB" : $"{bytes / 1_048_576.0:F1} MB";
    }

    public static long GetAvailableFreeSpace(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            SettingsService.Log($"Disk space check failed for '{path}': {ex.Message}");
            return long.MaxValue; // assume sufficient if check fails
        }
    }

    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;
        try
        {
            return Directory
                   .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                   .Sum(f =>
                   {
                       try
                       {
                           return new FileInfo(f).Length;
                       }
                       catch
                       {
                           /* file may have been deleted between enumeration and stat */
                           return 0L;
                       }
                   });
        }
        catch
        {
            /* unexpected error during directory enumeration – treat as zero to avoid blocking the caller */
            return 0;
        }
    }

    public static async IAsyncEnumerable<(string SectionName, long Bytes)> ScanAllAsync(
        IEnumerable<(string SectionName, string Dir)> apps,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (sectionName, dir) in apps)
        {
            if (ct.IsCancellationRequested)
                yield break;
            var bytes = await Task.Run(() => GetDirectorySize(dir), ct);
            yield return (sectionName, bytes);
        }
    }
}