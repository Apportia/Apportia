namespace Apportia.Services;

public static class PluginService
{
    private static readonly HashSet<string> JavaSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ghostscript",
        "GPG",
        "GPGLegacy32bit",
        "Java",
        "Java64",
        "JDK",
        "JDK64",
        "OpenJDK",
        "OpenJDK64",
        "OpenJDKJRE",
        "OpenJDKJRE64"
    };

    private static readonly HashSet<string> PluginSections =
        new(JavaSections.Append("Ghostscript"), StringComparer.OrdinalIgnoreCase);

    public static bool IsPlugin(string sectionName)
    {
        return PluginSections.Contains(sectionName);
    }

    public static bool IsJavaPlugin(string sectionName)
    {
        return JavaSections.Contains(sectionName);
    }

    public static string GetInstallDir(string sectionName = "")
    {
        return sectionName != string.Empty
            ? Path.Combine(AppDownloadService.AppsDir, "CommonFiles", sectionName)
            : Path.Combine(AppDownloadService.AppsDir, "CommonFiles");
    }

    public static string GetMarkerFile(string sectionName)
    {
        return Path.Combine(AppDownloadService.AppsDir, "CommonFiles", sectionName, "App", "AppInfo", "plugininstaller.ini");
    }
}