using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal partial class MirrorDatabaseJsonContext : JsonSerializerContext;

internal static class MirrorService
{
    private const string RemoteUrl =
        "https://raw.githubusercontent.com/Apportia/Apportia/main/data/mirror_database.json";

    private static readonly string CachePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "mirror_database.json");

    private static readonly string PrefsPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "mirrors.json");

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? _database;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal static async Task TryUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(RemoteUrl, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, json, ct);
            _database = null;
        }
        catch
        {
            /* keep existing cache intact on any failure */
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetDatabase()
    {
        if (_database != null)
            return _database;
        try
        {
            if (File.Exists(CachePath) &&
                JsonSerializer.Deserialize(
                        File.ReadAllText(CachePath),
                        typeof(Dictionary<string, Dictionary<string, string>>),
                        MirrorDatabaseJsonContext.Default)
                    is Dictionary<string, Dictionary<string, string>> dict)
            {
                _database = dict.ToDictionary(
                    kv => kv.Key, IReadOnlyDictionary<string, string> (kv) => kv.Value);
                return _database;
            }
        }
        catch
        {
            /* corrupt or missing cache */
        }

        _database = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        return _database;
    }

    internal static string? GetCurrentMirrorBase(string url)
    {
        return !Uri.TryCreate(url, UriKind.Absolute, out var uri) ? null : $"{uri.Scheme}://{uri.Host}";
    }

    internal static string ReplaceMirror(string url, string newBase)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var currentBase = $"{uri.Scheme}://{uri.Host}";
        return newBase + url[currentBase.Length..];
    }

    internal static IReadOnlyList<(string Base, string Label)> GetAvailableMirrors(string url)
    {
        foreach (var (prefix, mirrors) in GetDatabase())
        {
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            return mirrors.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        return [];
    }

    internal static string? LoadPreferredMirror()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                var val = File.ReadAllText(PrefsPath).Trim();
                return val.Length > 0 ? val : null;
            }
        }
        catch
        {
            /* corrupt or missing – no preferred mirror */
        }

        return null;
    }

    internal static void SavePreferredMirror(string mirror)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, mirror);
        }
        catch
        {
            /* non-critical – preference resets on next run */
        }
    }
}
