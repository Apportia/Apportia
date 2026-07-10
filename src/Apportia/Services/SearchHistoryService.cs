using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

public static class SearchHistoryService
{
    private const int MaxEntries = 20;

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "search_history.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, SearchHistoryJsonContext.Default.ListString) ?? [];
        }
        catch
        {
            /* corrupt or missing history file – start with empty list */
            return [];
        }
    }

    public static void Save(List<string> history)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(history, SearchHistoryJsonContext.Default.ListString));
        }
        catch
        {
            /* history persistence is non-critical */
        }
    }

    public static List<string> AddEntry(List<string> history, string entry)
    {
        var updated = new List<string>(history);
        updated.RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
        updated.Insert(0, entry);
        if (updated.Count > MaxEntries)
            updated.RemoveRange(MaxEntries, updated.Count - MaxEntries);
        return updated;
    }
}

[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class SearchHistoryJsonContext : JsonSerializerContext;