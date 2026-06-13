namespace Apportia.Models;

public sealed record AppEntry(
    string SectionName,
    string Name,
    string Description,
    string Category,
    string SubCategory,
    string DisplayVersion,
    string PackageVersion,
    string DownloadSize,
    string InstallSize,
    string DownloadFile,
    string DownloadPath,
    string Hash,
    string JoinedDate,
    string UpdateDate,
    string UserAgent,
    string Website,
    string Class = "",
    IReadOnlyDictionary<string, (string File, string Hash)>? LanguageVariants = null,
    bool RequiresJava = false
);