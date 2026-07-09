using System.Text.Json.Serialization;

namespace Apportia.Services;

public sealed record LocalAppVersion(string DisplayVersion, string PackageVersion);

// Facade over CurrentAppService – kept so existing call sites don't need to know about the
// consolidation into current_app_database.json.
public static class LocalVersionService
{
    public static IReadOnlyDictionary<string, LocalAppVersion> Load()
    {
        var db = CurrentAppService.LoadAll();
        var result = new Dictionary<string, LocalAppVersion>(db.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (section, info) in db)
        {
            if (string.IsNullOrEmpty(info.LocalPackageVersion) && string.IsNullOrEmpty(info.LocalDisplayVersion))
                continue;
            result[section] = new LocalAppVersion(info.LocalDisplayVersion, info.LocalPackageVersion);
        }

        return result;
    }

    public static void Save(string sectionName, string displayVersion, string packageVersion)
    {
        CurrentAppService.SetLocalVersion(sectionName, displayVersion, packageVersion);
    }

    public static void Remove(string sectionName)
    {
        // At uninstall this must fully drop the entry so a subsequent background sync doesn't
        // resurrect it as a phantom installed app.
        CurrentAppService.Remove(sectionName);
    }
}

[JsonSerializable(typeof(Dictionary<string, LocalAppVersion>))]
internal partial class LocalVersionJsonContext : JsonSerializerContext;