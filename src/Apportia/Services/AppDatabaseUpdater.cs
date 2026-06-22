using System.Text;
using System.Text.Json;

namespace Apportia.Services;

public static class AppDatabaseUpdater
{
    private const string RepoPath = "data/app_database.json";

    private const double MinEntryRatio = 0.8;
    private const int MinEntryCount = 100;

    public static readonly string CachePath = Path.Combine(ResolveDataDir(), "app_database.json");

    public static async Task TryUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GitHubContentClient.FetchTextAsync(RepoPath, ct);
            if (json is null || !IsValid(json))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, json, Encoding.UTF8, ct);
        }
        catch
        {
            /* keep existing cache intact on any failure */
        }
    }

    private static bool IsValid(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var count = doc.RootElement.EnumerateObject().Count();
            if (count < MinEntryCount)
                return false;
            if (!File.Exists(CachePath))
                return true;
            using var existing = JsonDocument.Parse(File.ReadAllText(CachePath));
            var existingCount = existing.RootElement.EnumerateObject().Count();
            return existingCount <= 0 || count >= existingCount * MinEntryRatio;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveDataDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "Data");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "Data");
    }
}