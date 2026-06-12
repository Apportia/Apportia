using System.Text.Json.Serialization;

namespace Apportia.Models;

public sealed class CustomAppInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ExeFile { get; set; } = string.Empty;
    public string AppUrl { get; set; } = string.Empty;
    public string Category { get; set; } = "Advanced";
    public string SubCategory { get; set; } = string.Empty;
    public string PackageVersion { get; set; } = string.Empty;
    public string VersionSource { get; set; } = string.Empty;
    public string UpdateDate { get; set; } = string.Empty;
}

[JsonSerializable(typeof(CustomAppInfo))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class CustomAppJsonContext : JsonSerializerContext;