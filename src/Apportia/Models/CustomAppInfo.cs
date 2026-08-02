using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Models;

[JsonConverter(typeof(CustomAppInfoConverter))]
public sealed class CustomAppInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ExeFile { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Category { get; set; } = "Advanced";
    public string SubCategory { get; set; } = string.Empty;
    public string JoinedDate { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string PackageVersion { get; set; } = string.Empty;
    public string VersionSource { get; set; } = string.Empty;
    public string UpdateDate { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public string DownloadFile { get; set; } = string.Empty;
    public bool UpdateEnabled { get; set; } = true;
    public string[] LeftoverKnown { get; set; } = [];
    public string[] LeftoverDelete { get; set; } = [];
}

public sealed class CustomAppInfoConverter : JsonConverter<CustomAppInfo>
{
    public override CustomAppInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();
        var info = new CustomAppInfo();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return info;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();
            var name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case nameof(CustomAppInfo.Name): info.Name = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.Description): info.Description = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.ExeFile): info.ExeFile = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.Website): info.Website = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.Category): info.Category = reader.GetString() ?? "Advanced"; break;
                case nameof(CustomAppInfo.SubCategory): info.SubCategory = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.JoinedDate): info.JoinedDate = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.DisplayVersion): info.DisplayVersion = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.PackageVersion): info.PackageVersion = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.VersionSource): info.VersionSource = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.UpdateDate): info.UpdateDate = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.DownloadPath): info.DownloadPath = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.DownloadFile): info.DownloadFile = reader.GetString() ?? ""; break;
                case nameof(CustomAppInfo.UpdateEnabled): info.UpdateEnabled = reader.GetBoolean(); break;
                case nameof(CustomAppInfo.LeftoverKnown): info.LeftoverKnown = ReadStringArray(ref reader); break;
                case nameof(CustomAppInfo.LeftoverDelete): info.LeftoverDelete = ReadStringArray(ref reader); break;
                default: reader.Skip(); break;
            }
        }

        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, CustomAppInfo value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteString(writer, nameof(value.Name), value.Name);
        WriteString(writer, nameof(value.Description), value.Description);
        WriteString(writer, nameof(value.ExeFile), value.ExeFile);
        WriteString(writer, nameof(value.Website), value.Website);
        if (value.Category != "Advanced")
            WriteString(writer, nameof(value.Category), value.Category);
        WriteString(writer, nameof(value.SubCategory), value.SubCategory);
        WriteString(writer, nameof(value.JoinedDate), value.JoinedDate);
        WriteString(writer, nameof(value.DisplayVersion), value.DisplayVersion);
        WriteString(writer, nameof(value.PackageVersion), value.PackageVersion);
        WriteString(writer, nameof(value.VersionSource), value.VersionSource);
        WriteString(writer, nameof(value.UpdateDate), value.UpdateDate);
        WriteString(writer, nameof(value.DownloadPath), value.DownloadPath);
        WriteString(writer, nameof(value.DownloadFile), value.DownloadFile);
        if (!value.UpdateEnabled)
            writer.WriteBoolean(nameof(value.UpdateEnabled), false);
        WriteArray(writer, nameof(value.LeftoverKnown), value.LeftoverKnown);
        WriteArray(writer, nameof(value.LeftoverDelete), value.LeftoverDelete);
        writer.WriteEndObject();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string value)
    {
        if (!string.IsNullOrEmpty(value))
            writer.WriteString(name, value);
    }

    private static void WriteArray(Utf8JsonWriter writer, string name, string[] values)
    {
        if (values.Length == 0)
            return;
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var v in values)
            writer.WriteStringValue(v);
        writer.WriteEndArray();
    }

    private static string[] ReadStringArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();
        var list = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return list.ToArray();
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException();
            list.Add(reader.GetString()!);
        }

        throw new JsonException();
    }
}

[JsonSerializable(typeof(CustomAppInfo))]
[JsonSerializable(typeof(Dictionary<string, CustomAppInfo>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class CustomAppJsonContext : JsonSerializerContext;
