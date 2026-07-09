using System.Text.Json.Serialization;

namespace Apportia.Models;

public sealed class CurrentAppInfo
{
    // Authoritative – set by Apportia install/update, never overwritten by upstream sync.
    public string LocalPackageVersion { get; set; } = string.Empty;
    public string LocalDisplayVersion { get; set; } = string.Empty;
    public string ExeFile { get; set; } = string.Empty;

    // Reflected from app_database.json – refreshed by background sync.
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SubCategory { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string PackageVersion { get; set; } = string.Empty;
    public string UpdateDate { get; set; } = string.Empty;
    public string DownloadFile { get; set; } = string.Empty;
    public string JoinedDate { get; set; } = string.Empty;
}

[JsonSerializable(typeof(CurrentAppInfo))]
[JsonSerializable(typeof(Dictionary<string, CurrentAppInfo>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class CurrentAppJsonContext : JsonSerializerContext;
