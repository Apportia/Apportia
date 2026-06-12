using System.Text.RegularExpressions;
using Apportia.Models;

namespace Apportia.Services;

public static class IniParser
{
    private static readonly (string From, string To)[] NameReplacements =
    [
        ("jdk", "JDK"),
        ("jPortable Launcher", "JRE Application Launcher"),
        ("jPortable", "JRE")
    ];

    private static readonly string[] NameFilterExclusions =
    [
        "PortableApps.com AppCompactor",
        "PortableApps.com Installer",
        "PortableApps.com Launcher"
    ];

    private static readonly string[] NameFilters =
    [
        "By PortableApps.com",
        ", Portable Edition",
        "Portable"
    ];

    private static readonly (string Word, string Replacement)[] DescriptionWordFixes =
    [
        ("windows", "Windows"),
        ("linux", "Linux"),
        ("aac", "AAC"),
        ("avi", "AVI"),
        ("dvd", "DVD"),
        ("flac", "FLAC"),
        ("ftp", "FTP"),
        ("gui", "GUI"),
        ("html", "HTML"),
        ("id3", "ID3"),
        ("lan", "LAN"),
        ("mkv", "MKV"),
        ("mp3", "MP3"),
        ("mp4", "MP4"),
        ("ogg", "OGG"),
        ("pdf", "PDF"),
        ("ssh", "SSH"),
        ("url", "URL"),
        ("usb", "USB"),
        ("vpn", "VPN"),
        ("wav", "WAV"),
        ("xml", "XML")
    ];

    private static readonly Dictionary<string, string> CategoryReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Graphics and Pictures"] = "Graphics & Pictures",
        ["Music and Video"] = "Music & Video"
    };

    private static readonly Dictionary<string, string> SubCategoryReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        // Casing fixes
        ["Download managers"] = "Download Managers",
        ["Font tools"] = "Font Tools",
        ["Registry tools"] = "Registry Tools",
        ["System tools"] = "System Tools",

        // "and" -> "&"
        ["Antivirus and Antispyware"] = "Antivirus & Antispyware",
        ["Audio Editors and Converters"] = "Audio Editors & Converters",
        ["CD/DVD Burning and Authoring"] = "CD/DVD Burning & Authoring",
        ["Drawing and Animation"] = "Drawing & Animation",
        ["File Compression and Packaging"] = "File Compression & Packaging",
        ["Music Creation and Notation"] = "Music Creation & Notation",
        ["Text Editors and IDEs"] = "Text Editors & IDEs",
        ["Video Editors and Converters"] = "Video Editors & Converters",

        // Renames – single label replaced with a cleaner name
        ["CAD (Computer-Aided Design)"] = "Computer-Aided Design",
        ["Consoles"] = "Terminal Emulators",
        ["Misc"] = "Runtime Components",
        ["Physics"] = "Simulation",

        // Consolidations – multiple labels collapsed into one canonical name
        ["Document Tools"] = "Document Viewers & Tools",
        ["Document Viewers"] = "Document Viewers & Tools",
        ["Journaling"] = "Note-Taking",
        ["On-Screen Keyboards"] = "Input Assistance",
        ["Password Generator"] = "Password Tools",
        ["Password Managers"] = "Password Tools",
        ["Podcast Receivers"] = "Podcasts & RSS",
        ["RSS Readers"] = "Podcasts & RSS",
        ["Screen Capture"] = "Screenshot Tools",
        ["Sticky Notes"] = "Note-Taking",
        ["Typing Assistance"] = "Input Assistance",
        ["Video Editors"] = "Video Editors & Converters",

        // Redirects – misplaced subcategory moved to the correct one
        ["Application Launcher"] = "Desktop Enhancement",
        ["Auditory"] = "Memorization",
        ["Cryptocurrency"] = "Financial",
        ["Data Recovery"] = "Disk Tools",
        ["File Analysis"] = "Disk Tools",
        ["Other"] = "Miscellaneous",
        ["Religion"] = "Miscellaneous",
        ["Servers"] = "Programming Environment",
        ["Social Media"] = "Chat",
        ["Spreadsheets"] = "Office Suites",
        ["Technical Computing"] = "Miscellaneous",
        ["Time Wasters"] = "Desktop Enhancement",
        ["Translation"] = "Miscellaneous",
        ["Web Editors"] = "Text Editors & IDEs"
    };

    private static readonly Dictionary<string, string> SubCategoryOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IObitUnlockerPortable"] = "Disk Tools",
        ["WhoDatPortable"] = "Networking",
        ["WhyNotWin11Portable"] = "System Tools",
        ["RedNotebookPortableLegacyx86"] = "Note-Taking"
    };

    public static AppInfoDetails ReadAppInfoDetails(string iniPath)
    {
        var name = string.Empty;
        var description = string.Empty;
        var category = string.Empty;
        var subCategory = string.Empty;
        var homepage = string.Empty;
        var packageVersion = string.Empty;

        try
        {
            var inDetails = false;
            var inVersion = false;
            foreach (var line in File.ReadLines(iniPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
                    continue;

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inDetails = trimmed.Equals("[Details]", StringComparison.OrdinalIgnoreCase);
                    inVersion = trimmed.Equals("[Version]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                var sep = trimmed.IndexOf('=');
                if (sep <= 0)
                    continue;

                var key = trimmed[..sep].Trim();
                var value = trimmed[(sep + 1)..].Trim();

                if (inDetails)
                {
                    if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        name = CleanName(value);
                    else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase))
                        description = value;
                    else if (key.Equals("Category", StringComparison.OrdinalIgnoreCase))
                        category = value;
                    else if (key.Equals("SubCategory", StringComparison.OrdinalIgnoreCase))
                        subCategory = value;
                    else if (key.Equals("Homepage", StringComparison.OrdinalIgnoreCase))
                        homepage = value;
                }
                else if (inVersion)
                {
                    if (key.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase))
                        packageVersion = value;
                }
            }
        }
        catch
        {
            /* unreadable ini – caller uses fallback defaults */
        }

        return new AppInfoDetails(name, description, category, subCategory, homepage, packageVersion);
    }

    public static IReadOnlyList<AppEntry> Parse(string filePath)
    {
        var entries = new List<AppEntry>();
        var currentSection = string.Empty;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(filePath))
            return entries;

        foreach (var line in File.ReadLines(filePath))
        {
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
                continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (currentSection.Length > 0)
                    entries.Add(BuildEntry(currentSection, values));

                currentSection = trimmed[1..^1];
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var sepIndex = trimmed.IndexOf('=');
            if (sepIndex <= 0)
                continue;
            var key = trimmed[..sepIndex].Trim();
            var value = trimmed[(sepIndex + 1)..].Trim();
            values.TryAdd(key, value);
        }

        if (currentSection.Length > 0)
            entries.Add(BuildEntry(currentSection, values));

        return entries;
    }

    private static string CleanName(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var name = raw;
        foreach (var (from, to) in NameReplacements)
            name = name.Replace(from, to, StringComparison.OrdinalIgnoreCase);

        var excluded = NameFilterExclusions.Any(ex =>
                                                    name.Equals(ex, StringComparison.OrdinalIgnoreCase));

        if (!excluded)
            name = NameFilters.Aggregate(name, (current, filter) =>
                                             current.Replace(filter, string.Empty, StringComparison.OrdinalIgnoreCase));

        while (name.Contains("  "))
            name = name.Replace("  ", " ");

        return name.TrimEnd(' ', ',');
    }

    private static string NormalizeDescription(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;
        var s = raw;
        foreach (var (word, replacement) in DescriptionWordFixes)
            s = Regex.Replace(s, $@"\b{word}\b", replacement, RegexOptions.IgnoreCase);
        return Capitalize(s);
    }

    private static string Capitalize(string? value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : char.ToUpper(value[0]) + value[1..];
    }

    private static AppEntry BuildEntry(string section, Dictionary<string, string> values)
    {
        values.TryGetValue("Name", out var name);
        values.TryGetValue("Description", out var description);
        values.TryGetValue("Category", out var category);
        values.TryGetValue("SubCategory", out var subCategory);
        if (category != null && CategoryReplacements.TryGetValue(category, out var mappedCategory))
            category = mappedCategory;
        var subCat = NormalizeSubCategory(section, subCategory);
        values.TryGetValue("DisplayVersion", out var displayVersion);
        values.TryGetValue("PackageVersion", out var packageVersion);
        values.TryGetValue("DownloadSize", out var downloadSize);
        values.TryGetValue("InstallSize", out var installSize);
        values.TryGetValue("DownloadFile", out var downloadFile);
        values.TryGetValue("DownloadPath", out var downloadPath);
        values.TryGetValue("Hash", out var hash);
        values.TryGetValue("ReleaseDate", out var releaseDate);
        values.TryGetValue("UpdateDate", out var updateDate);
        values.TryGetValue("URL", out var appUrl);
        values.TryGetValue("RequiresJava", out var requiresJava);

        downloadPath = downloadPath?.Replace("%WINDOWSVERSION%", "11");

        const string filePrefix = "DownloadFile_";
        const string hashPrefix = "Hash_";
        Dictionary<string, (string File, string Hash)>? languageVariants = null;
        foreach (var kv in values)
        {
            if (!kv.Key.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var lang = kv.Key[filePrefix.Length..];
            values.TryGetValue(hashPrefix + lang, out var langHash);
            languageVariants ??= new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            languageVariants[lang] = (kv.Value, langHash ?? string.Empty);
        }

        return new AppEntry(
            section,
            CleanName(name ?? section),
            NormalizeDescription(description),
            category ?? "Other",
            subCat,
            displayVersion ?? string.Empty,
            packageVersion ?? string.Empty,
            downloadSize ?? string.Empty,
            installSize ?? string.Empty,
            downloadFile ?? string.Empty,
            downloadPath ?? string.Empty,
            hash ?? string.Empty,
            NormalizeDate(releaseDate),
            NormalizeDate(updateDate),
            appUrl ?? string.Empty,
            languageVariants,
            string.Equals(requiresJava, "true", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string NormalizeSubCategory(string section, string? raw)
    {
        var value = raw ?? string.Empty;
        if (SubCategoryReplacements.TryGetValue(value, out var mapped))
            value = mapped;
        if (SubCategoryOverrides.TryGetValue(section, out var overridden))
            value = overridden;
        return value;
    }

    private static string NormalizeDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length >= 8 &&
            DateOnly.TryParseExact(digits[..8], "yyyyMMdd", out var date))
            return date.ToString("yyyy-MM-dd");

        // Fall back to standard parsing
        return DateTime.TryParse(raw, out var dt)
            ? dt.ToString("yyyy-MM-dd")
            : "1970-01-01";
    }

    public readonly record struct AppInfoDetails(
        string Name,
        string Description,
        string Category,
        string SubCategory,
        string Homepage,
        string PackageVersion);
}
