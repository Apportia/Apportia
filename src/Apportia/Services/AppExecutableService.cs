using System.Text.Json.Serialization;

namespace Apportia.Services;

// Facade over CurrentAppService – kept so existing call sites don't need to know about the
// consolidation into current_app_database.json.
public static class AppExecutableService
{
    public static void Save(string sectionName, string exeFileName, string defaultName)
    {
        var value = string.Equals(exeFileName, defaultName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : exeFileName;
        CurrentAppService.SetExeFile(sectionName, value);
    }

    public static void Remove(string sectionName)
    {
        // At uninstall this must fully drop the entry so a subsequent background sync doesn't
        // resurrect it as a phantom installed app.
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
            // Saved exe gone – clear the stale override and fall through
            CurrentAppService.SetExeFile(sectionName, string.Empty);
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

// Retained for the legacy migration in CurrentAppService.MigrateLegacyLayout.
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ExecutablesJsonContext : JsonSerializerContext;