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
    string ReleaseDate,
    string UpdateDate,
    string AppUrl,
    IReadOnlyDictionary<string, (string File, string Hash)>? LanguageVariants = null,
    bool RequiresJava = false
);