using System.Text;
using System.Text.Json;

namespace Apportia.Services;

public static class AppDatabaseUpdater
{
    private const string RepoPath = "data/app_database.json";

    private const double MinEntryRatio = 0.8;
    private const int MinEntryCount = 100;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    public static readonly string CachePath = Path.Combine(ResolveDataDir(), "app_database.json");

    public static async Task TryUpdateAsync(CancellationToken ct = default)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                var json = await GitHubClient.FetchFileContentAsync("Apportia/Apportia", RepoPath, ct: ct);
                if (json != null)
                {
                    if (!IsValid(json))
                        return;
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    await AtomicFile.WriteAllTextAsync(CachePath, json, Encoding.UTF8, ct);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                /* transient failure — fall through to retry */
            }

            if (attempt >= RetryDelays.Length)
                return;
            try
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
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