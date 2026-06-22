using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

public sealed record SecurityNotice(
    string Severity,
    string Category,
    string Title,
    string Notice,
    string Verified,
    IReadOnlyList<string> Alternatives
);

public static class SecurityNoticeService
{
    private const string RemoteUrl =
        "https://raw.githubusercontent.com/Apportia/Apportia/refs/heads/main/data/security_notices.json";

    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "security_notices.json");

    private static readonly HttpClient Http;

    private static Dictionary<string, RawNoticeEntry>? _db;

    static SecurityNoticeService()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Wget", "1.25"));
    }

    public static async Task TryUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await TryFetchAsync(ct);
            if (json is null || !IsValid(json))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, json, Encoding.UTF8, ct);
            _db = null;
        }
        catch
        {
            /* keep existing cache intact on any failure */
        }
    }

    private static async Task<string?> TryFetchAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(RemoteUrl, ct);
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
            return doc.RootElement.EnumerateObject().Count() >= 10;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, RawNoticeEntry> GetDatabase()
    {
        if (_db != null)
            return _db;
        try
        {
            if (File.Exists(FilePath) &&
                JsonSerializer.Deserialize(
                        File.ReadAllText(FilePath),
                        typeof(Dictionary<string, RawNoticeEntry>),
                        SecurityNoticesJsonContext.Default)
                    is Dictionary<string, RawNoticeEntry> loaded)
            {
                _db = loaded;
                return _db;
            }
        }
        catch
        {
            /* corrupt or missing – return empty database */
        }

        _db = [];
        return _db;
    }

    public static SecurityNotice? Resolve(string key)
    {
        var db = GetDatabase();
        if (!db.TryGetValue(key, out var entry))
            return null;

        IReadOnlyList<string> ownAlternatives = entry.Alternatives ?? [];

        var full = entry;
        if (entry.Ref != null && db.TryGetValue(entry.Ref, out var refEntry) && refEntry.Ref == null)
            full = refEntry;

        if (full.Severity == null || full.Title == null || full.Notice == null)
            return null;

        return new SecurityNotice(
            full.Severity,
            full.Category ?? string.Empty,
            full.Title,
            full.Notice,
            full.Verified ?? string.Empty,
            ownAlternatives
        );
    }
}

internal sealed class RawNoticeEntry
{
    [JsonPropertyName("Ref")] public string? Ref { get; set; }
    [JsonPropertyName("Severity")] public string? Severity { get; set; }
    [JsonPropertyName("Category")] public string? Category { get; set; }
    [JsonPropertyName("Title")] public string? Title { get; set; }
    [JsonPropertyName("Notice")] public string? Notice { get; set; }
    [JsonPropertyName("Verified")] public string? Verified { get; set; }
    [JsonPropertyName("Alternatives")] public List<string>? Alternatives { get; set; }
}

[JsonSerializable(typeof(Dictionary<string, RawNoticeEntry>))]
[JsonSerializable(typeof(RawNoticeEntry))]
[JsonSerializable(typeof(List<string>))]
internal partial class SecurityNoticesJsonContext : JsonSerializerContext;
