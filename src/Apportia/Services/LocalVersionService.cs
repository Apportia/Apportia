using System.Text.Json.Serialization;

namespace Apportia.Services;

public sealed record LocalAppVersion(string DisplayVersion, string PackageVersion);

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
        CurrentAppService.Remove(sectionName);
    }
}

[JsonSerializable(typeof(Dictionary<string, LocalAppVersion>))]
internal partial class LocalVersionJsonContext : JsonSerializerContext;