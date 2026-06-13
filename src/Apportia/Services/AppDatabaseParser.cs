using System.Text.Json;
using System.Text.Json.Serialization;
using Apportia.Models;

namespace Apportia.Services;

public static class AppDatabaseParser
{
    public static IReadOnlyList<AppEntry> ParseJson(string filePath)
    {
        var entries = new List<AppEntry>();
        if (!File.Exists(filePath))
            return entries;

        try
        {
            var json = File.ReadAllText(filePath);
            if (JsonSerializer.Deserialize(
                    json,
                    typeof(Dictionary<string, Dictionary<string, string?>>),
                    AppDatabaseJsonContext.Default)
                is not Dictionary<string, Dictionary<string, string?>> dict)
                return entries;

            const string filePrefix = "DownloadFile_";
            const string hashPrefix = "Hash_";

            foreach (var (section, values) in dict)
            {
                values.TryGetValue("Name", out var name);
                values.TryGetValue("Description", out var description);
                values.TryGetValue("Category", out var category);
                values.TryGetValue("SubCategory", out var subCategory);
                values.TryGetValue("DisplayVersion", out var displayVersion);
                values.TryGetValue("PackageVersion", out var packageVersion);
                values.TryGetValue("DownloadSize", out var downloadSize);
                values.TryGetValue("InstallSize", out var installSize);
                values.TryGetValue("DownloadFile", out var downloadFile);
                values.TryGetValue("DownloadPath", out var downloadPath);
                values.TryGetValue("Hash", out var hash);
                values.TryGetValue("ReleaseDate", out var releaseDate);
                values.TryGetValue("UpdateDate", out var updateDate);
                values.TryGetValue("UserAgent", out var userAgent);
                values.TryGetValue("Website", out var website);
                values.TryGetValue("Class", out var cls);
                values.TryGetValue("RequiresJava", out var requiresJava);

                Dictionary<string, (string File, string Hash)>? languageVariants = null;
                foreach (var kv in values)
                {
                    if (!kv.Key.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var lang = kv.Key[filePrefix.Length..];
                    values.TryGetValue(hashPrefix + lang, out var langHash);
                    languageVariants ??= new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                    languageVariants[lang] = (kv.Value ?? string.Empty, langHash ?? string.Empty);
                }

                entries.Add(new AppEntry(
                                section,
                                name ?? section,
                                description ?? string.Empty,
                                category ?? "Other",
                                subCategory ?? string.Empty,
                                displayVersion ?? string.Empty,
                                packageVersion ?? string.Empty,
                                downloadSize ?? string.Empty,
                                installSize ?? string.Empty,
                                downloadFile ?? string.Empty,
                                downloadPath ?? string.Empty,
                                hash ?? string.Empty,
                                releaseDate ?? string.Empty,
                                updateDate ?? string.Empty,
                                userAgent ?? string.Empty,
                                website ?? string.Empty,
                                cls ?? string.Empty,
                                languageVariants,
                                string.Equals(requiresJava, "true", StringComparison.OrdinalIgnoreCase)
                            ));
            }
        }
        catch
        {
            /* corrupt or missing JSON – caller uses existing cache */
        }

        return entries;
    }
}

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string?>>))]
internal partial class AppDatabaseJsonContext : JsonSerializerContext;
