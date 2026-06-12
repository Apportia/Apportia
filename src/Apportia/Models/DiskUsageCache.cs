using System.Text.Json.Serialization;

namespace Apportia.Models;

public sealed class DiskUsageCache
{
    public Dictionary<string, long> Sizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonSerializable(typeof(DiskUsageCache))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class DiskUsageCacheJsonContext : JsonSerializerContext;