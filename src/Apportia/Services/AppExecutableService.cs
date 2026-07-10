using System.Text.Json.Serialization;

namespace Apportia.Services;

public static class AppExecutableService
{
    public static void Save(string sectionName, string exeFileName, string defaultName)
    {
        _ = defaultName;
        CurrentAppService.SetExeFile(sectionName, exeFileName);
    }

    public static void Remove(string sectionName)
    {
        // Fully drop the entry so a later sync doesn't resurrect it as a phantom install.
        CurrentAppService.Remove(sectionName);
    }

    public static (string? ExePath, string[] Candidates) Resolve(string appDir, string sectionName)
    {
        var defaultName = sectionName + ".exe";
        var defaultPath = Path.Combine(appDir, defaultName);

        var saved = CurrentAppService.GetExeFile(sectionName);
        if (!string.IsNullOrEmpty(saved))
        {
            var savedPath = Path.Combine(appDir, saved);
            if (File.Exists(savedPath))
                return (savedPath, []);
            CurrentAppService.SetExeFile(sectionName, string.Empty);
        }

        if (File.Exists(defaultPath))
        {
            Save(sectionName, defaultName, defaultName);
            return (defaultPath, []);
        }

        if (!Directory.Exists(appDir))
            return (null, []);

        var candidates = Directory.GetFiles(appDir, "*.exe", SearchOption.TopDirectoryOnly);

        switch (candidates.Length)
        {
            case 1:
            {
                var exePath = candidates[0];
                Save(sectionName, Path.GetFileName(exePath), defaultName);
                return (exePath, []);
            }
            case > 1:
                return (null, candidates.Select(c => Path.GetFileName(c)).ToArray());
            default:
                return (null, []);
        }
    }
}

// Used only by CurrentAppService.MigrateLegacyLayout.
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ExecutablesJsonContext : JsonSerializerContext;