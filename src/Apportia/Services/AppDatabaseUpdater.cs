using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Apportia.Services;

public sealed class AppDatabaseUpdater : IDisposable
{
    private const string PrimaryUrl = "https://raw.githubusercontent.com/Apportia/Apportia/main/data/app_database.json";

    private const double MinEntryRatio = 0.8;
    private const int MinEntryCount = 100;

    public static readonly string CachePath = Path.Combine(ResolveDataDir(), "app_database.json");

    private readonly HttpClient _http;

    public AppDatabaseUpdater()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Wget", "1.25"));
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async Task TryUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await TryFetchPrimaryAsync(ct);
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

    private async Task<string?> TryFetchPrimaryAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(PrimaryUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            /* network or parse failure – caller keeps existing cache */
            return null;
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
