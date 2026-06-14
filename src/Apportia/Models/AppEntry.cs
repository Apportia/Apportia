namespace Apportia.Models;

public sealed record AppEntry(
    string SectionName,
    string Name,
    string Description,
    string Website,
    string Category,
    string SubCategory,
    string JoinedDate,
    string DisplayVersion,
    string PackageVersion,
    string UpdateDate,
    string DownloadFile,
    string Hash,
    string DownloadPath,
    string UserAgent,
    string DownloadSize,
    string InstallSize,
    string Class = "",
    IReadOnlyDictionary<string, (string File, string Hash)>? LanguageVariants = null,
    bool RequiresJava = false
);