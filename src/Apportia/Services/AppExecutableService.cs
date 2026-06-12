using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

public static class AppExecutableService
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "executables.json");

    private static Dictionary<string, string> LoadDict()
    {
        if (!File.Exists(FilePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, ExecutablesJsonContext.Default.DictionaryStringString)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            /* corrupt or missing cache file – start with empty mapping */
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteDict(Dictionary<string, string> dict)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(dict, ExecutablesJsonContext.Default.DictionaryStringString);
        File.WriteAllText(FilePath, json);
    }

    public static void Save(string sectionName, string exeFileName, string defaultName)
    {
        var dict = LoadDict();
        if (string.Equals(exeFileName, defaultName, StringComparison.OrdinalIgnoreCase))
            dict.Remove(sectionName);
        else
            dict[sectionName] = exeFileName;
        WriteDict(dict);
    }

    public static void Remove(string sectionName)
    {
        if (!File.Exists(FilePath))
            return;
        var dict = LoadDict();
        if (dict.Remove(sectionName))
            WriteDict(dict);
    }

    public static (string? ExePath, string[] Candidates) Resolve(string appDir, string sectionName)
    {
        var defaultName = sectionName + ".exe";
        var defaultPath = Path.Combine(appDir, defaultName);

        // Saved override
        var dict = LoadDict();
        if (dict.TryGetValue(sectionName, out var saved))
        {
            var savedPath = Path.Combine(appDir, saved);
            if (File.Exists(savedPath))
                return (savedPath, []);
            // Saved exe gone – remove stale entry and fall through
            dict.Remove(sectionName);
            WriteDict(dict);
        }

        if (File.Exists(defaultPath))
            return (defaultPath, []);

        if (!Directory.Exists(appDir))
            return (null, []);

        var candidates = Directory.GetFiles(appDir, "*.exe", SearchOption.TopDirectoryOnly);

        switch (candidates.Length)
        {
            case 1:
            {
                var exePath = candidates[0];
                var exeName = Path.GetFileName(exePath);
                if (!string.Equals(exeName, defaultName, StringComparison.OrdinalIgnoreCase))
                    Save(sectionName, exeName, defaultName);
                return (exePath, []);
            }
            case > 1:
                return (null, candidates.Select(c => Path.GetFileName(c)).ToArray());
            default:
                return (null, []);
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ExecutablesJsonContext : JsonSerializerContext;
