using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class MirrorDatabaseJsonContext : JsonSerializerContext;

internal static class MirrorService
{
    private const string RepoPath = "data/mirror_database.json";

    private static readonly string CachePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "mirror_database.json");

    private static readonly string PrefsPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "preferred_mirrors.json");

    private static readonly Lock DatabaseLock = new();
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? _database;

    internal static async Task TryUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GitHubClient.FetchFileContentAsync("Apportia/Apportia", RepoPath, ct: ct);
            if (json is null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await AtomicFile.WriteAllTextAsync(CachePath, json, ct: ct);
            lock (DatabaseLock)
            {
                _database = null;
            }
        }
        catch
        {
            /* keep existing cache intact on any failure */
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetDatabase()
    {
        lock (DatabaseLock)
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
    }

    private static bool IsSourceForgePrefix(string prefix)
    {
        return prefix.EndsWith("sourceforge.net", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetGroupPrefix(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        foreach (var (prefix, _) in GetDatabase())
        {
            var parts = prefix.Split('.');
            if (parts.Length < 2)
                continue;
            var baseDomain = string.Join('.', parts[^2..]);
            if (uri.Host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase))
                return prefix;
        }

        return null;
    }

    internal static string? GetCurrentMirrorSlug(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var prefix = GetGroupPrefix(url);
        if (prefix == null)
            return null;

        if (!IsSourceForgePrefix(prefix))
            return uri.Host.Split('.')[0];

        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            if (part.StartsWith("use_mirror=", StringComparison.OrdinalIgnoreCase))
                return part["use_mirror=".Length..];
        }

        return "downloads";
    }

    internal static string ApplyMirror(string url, string slug)
    {
        var prefix = GetGroupPrefix(url);
        if (prefix == null)
            return url;

        if (IsSourceForgePrefix(prefix))
        {
            var baseUrl = url.Contains('?') ? url[..url.IndexOf('?')] : url;
            return slug == "downloads" ? baseUrl : $"{baseUrl}?use_mirror={slug}";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var prefixParts = prefix.Split('.');
        var baseDomain = string.Join('.', prefixParts[^2..]);
        return $"{uri.Scheme}://{slug}.{baseDomain}{uri.PathAndQuery}";
    }

    internal static IReadOnlyList<(string Slug, string Label)> GetAvailableMirrors(string url)
    {
        var prefix = GetGroupPrefix(url);
        if (prefix == null || !GetDatabase().TryGetValue(prefix, out var mirrors))
            return [];
        return mirrors.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    internal static string? LoadPreferredMirror(string url)
    {
        var prefix = GetGroupPrefix(url);
        if (prefix == null)
            return null;
        try
        {
            if (File.Exists(PrefsPath) &&
                JsonSerializer.Deserialize(
                        File.ReadAllText(PrefsPath),
                        typeof(Dictionary<string, string>),
                        MirrorDatabaseJsonContext.Default)
                    is Dictionary<string, string> prefs &&
                prefs.TryGetValue(prefix, out var slug))
                return slug;
        }
        catch
        {
            /* corrupt or missing – no preferred mirror */
        }

        return null;
    }

    internal static void SavePreferredMirror(string url, string slug)
    {
        var prefix = GetGroupPrefix(url);
        if (prefix == null)
            return;
        try
        {
            Dictionary<string, string> prefs = [];
            if (File.Exists(PrefsPath) &&
                JsonSerializer.Deserialize(
                        File.ReadAllText(PrefsPath),
                        typeof(Dictionary<string, string>),
                        MirrorDatabaseJsonContext.Default)
                    is Dictionary<string, string> existing)
                prefs = existing;
            prefs[prefix] = slug;
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            AtomicFile.WriteAllText(PrefsPath,
                                    JsonSerializer.Serialize(prefs, typeof(Dictionary<string, string>), MirrorDatabaseJsonContext.Default));
        }
        catch
        {
            /* non-critical – preference resets on next run */
        }
    }
}