using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

public static class AppLanguageService
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "languages.json");

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SimpChinese"] = "Chinese (Simplified)",
        ["TradChinese"] = "Chinese (Traditional)",
        ["PortugueseBR"] = "Portuguese (Brazil)",
        ["EnglishGB"] = "English (UK)",
        ["SpanishInternational"] = "Spanish (International)",
        ["NorwegianNynorsk"] = "Norwegian (Nynorsk)"
    };

    public static string FormatLanguageName(string key)
    {
        if (DisplayNames.TryGetValue(key, out var display))
            return display;

        var sb = new StringBuilder(key.Length + 4);
        foreach (var ch in key)
        {
            if (char.IsUpper(ch) && sb.Length > 0)
                sb.Append(' ');
            sb.Append(ch);
        }

        return sb.ToString();
    }

    public static string? Load(string sectionName)
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var dict = JsonSerializer.Deserialize(
                File.ReadAllText(FilePath),
                LanguagesJsonContext.Default.DictionaryStringString);
            return dict != null && dict.TryGetValue(sectionName, out var lang) ? lang : null;
        }
        catch
        {
            /* corrupt or missing – fall back to prompting the user */
            return null;
        }
    }

    public static void Save(string sectionName, string language)
    {
        try
        {
            Dictionary<string, string> dict;
            try
            {
                dict = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize(
                          File.ReadAllText(FilePath),
                          LanguagesJsonContext.Default.DictionaryStringString)
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                /* corrupt or missing – start with an empty dictionary so the new entry is not lost */
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            dict[sectionName] = language;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(dict, LanguagesJsonContext.Default.DictionaryStringString));
        }
        catch
        {
            /* a failed preference save must not abort the install */
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class LanguagesJsonContext : JsonSerializerContext;