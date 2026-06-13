namespace Apportia.Models;

public readonly record struct AppInfo(
    string Name,
    string Description,
    string Category,
    string SubCategory,
    string Homepage,
    string PackageVersion);