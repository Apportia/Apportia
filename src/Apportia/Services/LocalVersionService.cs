using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

public sealed record LocalAppVersion(string DisplayVersion, string PackageVersion);

public static class LocalVersionService
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "local_app_versions.json");

    private static Dictionary<string, LocalAppVersion>? _cache;

    public static IReadOnlyDictionary<string, LocalAppVersion> Load()
    {
        if (_cache != null)
            return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                _cache = JsonSerializer.Deserialize(text, LocalVersionJsonContext.Default.DictionaryStringLocalAppVersion);
            }
        }
        catch
        {
            /* corrupt or missing file — start fresh */
        }

        _cache ??= new Dictionary<string, LocalAppVersion>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    public static void Save(string sectionName, string displayVersion, string packageVersion)
    {
        Load();
        _cache![sectionName] = new LocalAppVersion(displayVersion, packageVersion);
        Persist();
    }

    public static void Remove(string sectionName)
    {
        Load();
        if (_cache!.Remove(sectionName))
            Persist();
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                              JsonSerializer.Serialize(_cache, LocalVersionJsonContext.Default.DictionaryStringLocalAppVersion));
        }
        catch
        {
            /* non-critical */
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, LocalAppVersion>))]
internal partial class LocalVersionJsonContext : JsonSerializerContext;
