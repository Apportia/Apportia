using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

public static class AppUsageService
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, DateTime>? _cache;

    private static string DatabasePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "app_usage.json");

    public static event EventHandler<string>? Changed;

    public static DateTime? GetLastRun(string sectionName)
    {
        var db = Load();
        return db.TryGetValue(sectionName, out var d) ? d : null;
    }

    public static void RecordLaunch(string sectionName)
    {
        if (string.IsNullOrEmpty(sectionName))
            return;
        lock (Gate)
        {
            var db = LoadUnlocked();
            db[sectionName] = DateTime.UtcNow;
            Save(db);
        }

        Changed?.Invoke(null, sectionName);
    }

    public static void Remove(string sectionName)
    {
        lock (Gate)
        {
            var db = LoadUnlocked();
            if (db.Remove(sectionName))
                Save(db);
        }
    }

    private static Dictionary<string, DateTime> Load()
    {
        lock (Gate)
        {
            return LoadUnlocked();
        }
    }

    private static Dictionary<string, DateTime> LoadUnlocked()
    {
        if (_cache is not null)
            return _cache;
        try
        {
            if (File.Exists(DatabasePath))
            {
                var dict = JsonSerializer.Deserialize(
                    File.ReadAllText(DatabasePath),
                    AppUsageJsonContext.Default.DictionaryStringDateTime);
                if (dict is not null)
                {
                    _cache = new Dictionary<string, DateTime>(dict, StringComparer.OrdinalIgnoreCase);
                    return _cache;
                }
            }
        }
        catch
        {
            // corrupt file: start fresh, next save overwrites
        }

        _cache = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private static void Save(Dictionary<string, DateTime> dict)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            var tmp = DatabasePath + ".tmp";
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(dict, AppUsageJsonContext.Default.DictionaryStringDateTime));
            File.Move(tmp, DatabasePath, true);
            _cache = dict;
        }
        catch
        {
            // usage tracking is best-effort; disk failure must not disrupt launches
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, DateTime>))]
internal partial class AppUsageJsonContext : JsonSerializerContext;
