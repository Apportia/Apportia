using Apportia.Models;

namespace Apportia.Services;

public static class CategoryManager
{
    public const string Advanced = "Advanced";
    public const string Legacy = "Legacy";

    private static readonly string[] AdvancedKeywords =
    [
        " ESR",
        " LTS",
        " Test",
        " via ",
        ", Address Book",
        "2nd Profile",
        "AppRenamed",
        "Beta",
        "Chrome Dev",
        "Classic",
        "Compat",
        "Developer Edition",
        "Ghostscript",
        "GPG",
        "GPGLegacy32bit",
        "Incognito Mode",
        "JDK",
        "jdkPortable",
        "jPortable",
        "JRE",
        "LibreOffice Previous",
        "Nightly",
        "Notepad2-mod",
        "OpenJDK",
        "Portable Dev",
        "PortableApps.com",
        "Private Browsing",
        "Private Window",
        "qBittorrent Enhanced"
    ];

    private static readonly string[] LegacyKeywords =
    [
        " Retro",
        "Discontinued",
        "Legacy",
        "FirefoxPortableNightly64",
        "PrivateBrowsingByPortableApps"
    ];

    public static IReadOnlyList<AppEntry> Filter(IReadOnlyList<AppEntry> entries)
    {
        return entries
               .Where(e => !string.Equals(e.Category, "None", StringComparison.OrdinalIgnoreCase))
               .ToList();
    }

    public static bool IsAdvanced(AppEntry entry)
    {
        if (IsLegacy(entry))
            return false;
        return AdvancedKeywords.Any(kw =>
                                        entry.SectionName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                                        entry.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                                        entry.DisplayVersion.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLegacy(AppEntry entry)
    {
        return LegacyKeywords.Any(kw =>
                                      entry.SectionName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                                      entry.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                                      entry.DisplayVersion.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }
}
