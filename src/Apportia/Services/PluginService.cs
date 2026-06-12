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

    public static string GetInstallDir(string appsBaseDir, string sectionName)
    {
        return Path.Combine(appsBaseDir, "CommonFiles", sectionName);
    }

    public static string GetMarkerFile(string appsBaseDir, string sectionName)
    {
        return Path.Combine(appsBaseDir, "CommonFiles", sectionName, "App", "AppInfo", "plugininstaller.ini");
    }
}
