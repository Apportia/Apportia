using System.Text.RegularExpressions;

namespace Apportia.Services;

public static partial class AppExecutableService
{
    public static void Save(string sectionName, string exeFileName, string defaultName)
    {
        _ = defaultName;
        CurrentAppService.SetExeFile(sectionName, exeFileName);
    }

    public static void Remove(string sectionName)
    {
        // Lest the next sync re-add it as a phantom install.
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

        var defaultJarName = sectionName + ".jar";
        var defaultJarPath = Path.Combine(appDir, defaultJarName);
        if (File.Exists(defaultJarPath))
        {
            Save(sectionName, defaultJarName, defaultName);
            return (defaultJarPath, []);
        }

        if (!Directory.Exists(appDir))
            return (null, []);

        var candidates = Directory.GetFiles(appDir, "*.exe", SearchOption.TopDirectoryOnly)
                                  .Concat(Directory.GetFiles(appDir, "*.jar", SearchOption.TopDirectoryOnly))
                                  .ToArray();

        switch (candidates.Length)
        {
            case 1:
            {
                var exePath = candidates[0];
                Save(sectionName, Path.GetFileName(exePath), defaultName);
                return (exePath, []);
            }
            case > 1:
                return (null, [
                    .. candidates
                       .Select(c => Path.GetFileName(c))
                       .OrderBy(CandidateSortKey)
                ]);
            default:
                return (null, []);
        }
    }

    private static (int Secondary, int Annotated, int Length, string Name) CandidateSortKey(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var secondary = SecondaryTokenPattern().IsMatch(stem) ? 1 : 0;
        var annotated = stem.IndexOfAny(['-', '_']) >= 0 ? 1 : 0;
        return (secondary, annotated, stem.Length, name);
    }

    public static bool IsSecondaryName(string fileName)
    {
        return SecondaryTokenPattern().IsMatch(Path.GetFileNameWithoutExtension(fileName));
    }

    public static bool IsAnnotatedName(string fileName)
    {
        return Path.GetFileNameWithoutExtension(fileName).IndexOfAny(['-', '_']) >= 0;
    }

    [GeneratedRegex(@"(^|[^A-Za-z])(cli|helper)([^A-Za-z]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SecondaryTokenPattern();
}
