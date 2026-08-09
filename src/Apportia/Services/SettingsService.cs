using System.Text.Json;
using System.Text.Json.Serialization;
using Apportia.Text;

namespace Apportia.Services;

public enum CategoryScope
{
    Standard,
    Extended,
    Full
}

public enum CategoryDisplayMode
{
    Full,
    Categories,
    None
}

public enum InstallFilter
{
    All,
    Installed,
    NotInstalled
}

[JsonConverter(typeof(FilterViewSettingsConverter))]
public sealed class FilterViewSettings
{
    public static readonly FilterViewSettings Default = new();

    public CategoryDisplayMode CategoryDisplay { get; set; } = CategoryDisplayMode.Full;
    public CategoryScope CategoryScope { get; set; } = CategoryScope.Standard;
    public int FontSize { get; set; } = 13;
    public int IconSize { get; set; } = 24;
    public bool IsGridView { get; set; }
    public double WindowWidth { get; set; } = 1024;
    public double WindowHeight { get; set; } = 720;
    public string SortColumn { get; set; } = "Name";
    public bool SortDescending { get; set; }
}

[JsonConverter(typeof(AppSettingsConverter))]
public sealed class AppSettings
{
    public double ColumnName { get; set; } = 200;
    public double ColumnVersion { get; set; } = 90;
    public double ColumnDownload { get; set; } = 85;
    public double ColumnInstall { get; set; } = 80;
    public double ColumnJoined { get; set; } = 90;
    public double ColumnUpdated { get; set; } = 90;
    public double ColumnUsed { get; set; } = 75;
    public string Theme { get; set; } = "Default";
    public string ThemeSource { get; set; } = "Native";
    public bool HasShownTips { get; set; }
    public bool LinuxSetupCompleted { get; set; }
    public bool LinuxThemeInfoShown { get; set; }
    public string WineMode { get; set; } = "System";
    public string WineVersion { get; set; } = "latest";
    public bool WineInstallFonts { get; set; } = true;
    public bool WineApplyTheme { get; set; } = true;
    public Dictionary<string, FilterViewSettings> ViewPresets { get; set; } = new();
}

public sealed class FilterViewSettingsConverter : JsonConverter<FilterViewSettings>
{
    public override FilterViewSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();
        var v = new FilterViewSettings();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return v;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();
            var name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case nameof(FilterViewSettings.CategoryDisplay): v.CategoryDisplay = (CategoryDisplayMode)reader.GetInt32(); break;
                case nameof(FilterViewSettings.CategoryScope): v.CategoryScope = (CategoryScope)reader.GetInt32(); break;
                case nameof(FilterViewSettings.FontSize): v.FontSize = reader.GetInt32(); break;
                case nameof(FilterViewSettings.IconSize): v.IconSize = reader.GetInt32(); break;
                case nameof(FilterViewSettings.IsGridView): v.IsGridView = reader.GetBoolean(); break;
                case nameof(FilterViewSettings.WindowWidth): v.WindowWidth = reader.GetDouble(); break;
                case nameof(FilterViewSettings.WindowHeight): v.WindowHeight = reader.GetDouble(); break;
                case nameof(FilterViewSettings.SortColumn): v.SortColumn = reader.GetString() ?? "Name"; break;
                case nameof(FilterViewSettings.SortDescending): v.SortDescending = reader.GetBoolean(); break;
                default: reader.Skip(); break;
            }
        }

        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, FilterViewSettings v, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (v.CategoryDisplay != CategoryDisplayMode.Full)
            writer.WriteNumber(nameof(v.CategoryDisplay), (int)v.CategoryDisplay);
        if (v.CategoryScope != CategoryScope.Standard)
            writer.WriteNumber(nameof(v.CategoryScope), (int)v.CategoryScope);
        if (v.FontSize != 13)
            writer.WriteNumber(nameof(v.FontSize), v.FontSize);
        if (v.IconSize != 24)
            writer.WriteNumber(nameof(v.IconSize), v.IconSize);
        if (v.IsGridView)
            writer.WriteBoolean(nameof(v.IsGridView), true);
        if (Math.Abs(v.WindowWidth - 1024) > 1)
            writer.WriteNumber(nameof(v.WindowWidth), v.WindowWidth);
        if (Math.Abs(v.WindowHeight - 720) > 1)
            writer.WriteNumber(nameof(v.WindowHeight), v.WindowHeight);
        if (v.SortColumn != "Name")
            writer.WriteString(nameof(v.SortColumn), v.SortColumn);
        if (v.SortDescending)
            writer.WriteBoolean(nameof(v.SortDescending), true);
        writer.WriteEndObject();
    }
}

public sealed class AppSettingsConverter : JsonConverter<AppSettings>
{
    public override AppSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException();
        var s = new AppSettings();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return s;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();
            var name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case nameof(AppSettings.ColumnName): s.ColumnName = reader.GetDouble(); break;
                case nameof(AppSettings.ColumnVersion): s.ColumnVersion = reader.GetDouble(); break;
                case nameof(AppSettings.ColumnDownload): s.ColumnDownload = reader.GetDouble(); break;
                case nameof(AppSettings.ColumnInstall): s.ColumnInstall = reader.GetDouble(); break;
                case nameof(AppSettings.ColumnJoined): s.ColumnJoined = reader.GetDouble(); break;
                case nameof(AppSettings.ColumnUpdated): s.ColumnUpdated = reader.GetDouble(); break;
                case nameof(AppSettings.ColumnUsed): s.ColumnUsed = reader.GetDouble(); break;
                case nameof(AppSettings.Theme): s.Theme = reader.GetString() ?? "Default"; break;
                case nameof(AppSettings.ThemeSource): s.ThemeSource = reader.GetString() ?? "Native"; break;
                case nameof(AppSettings.HasShownTips): s.HasShownTips = reader.GetBoolean(); break;
                case nameof(AppSettings.LinuxSetupCompleted): s.LinuxSetupCompleted = reader.GetBoolean(); break;
                case nameof(AppSettings.LinuxThemeInfoShown): s.LinuxThemeInfoShown = reader.GetBoolean(); break;
                case nameof(AppSettings.WineMode): s.WineMode = reader.GetString() ?? "System"; break;
                case nameof(AppSettings.WineVersion): s.WineVersion = reader.GetString() ?? "latest"; break;
                case nameof(AppSettings.WineInstallFonts): s.WineInstallFonts = reader.GetBoolean(); break;
                case nameof(AppSettings.WineApplyTheme): s.WineApplyTheme = reader.GetBoolean(); break;
                case nameof(AppSettings.ViewPresets):
                    s.ViewPresets = JsonSerializer.Deserialize(
                                        ref reader, SettingsJsonContext.Default.DictionaryStringFilterViewSettings)
                                    ?? new Dictionary<string, FilterViewSettings>();
                    break;
                default: reader.Skip(); break;
            }
        }

        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, AppSettings s, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (Math.Abs(s.ColumnName - 200) > 1)
            writer.WriteNumber(nameof(s.ColumnName), s.ColumnName);
        if (Math.Abs(s.ColumnVersion - 90) > 1)
            writer.WriteNumber(nameof(s.ColumnVersion), s.ColumnVersion);
        if (Math.Abs(s.ColumnDownload - 85) > 1)
            writer.WriteNumber(nameof(s.ColumnDownload), s.ColumnDownload);
        if (Math.Abs(s.ColumnInstall - 80) > 1)
            writer.WriteNumber(nameof(s.ColumnInstall), s.ColumnInstall);
        if (Math.Abs(s.ColumnJoined - 90) > 1)
            writer.WriteNumber(nameof(s.ColumnJoined), s.ColumnJoined);
        if (Math.Abs(s.ColumnUpdated - 90) > 1)
            writer.WriteNumber(nameof(s.ColumnUpdated), s.ColumnUpdated);
        if (Math.Abs(s.ColumnUsed - 75) > 1)
            writer.WriteNumber(nameof(s.ColumnUsed), s.ColumnUsed);
        if (s.Theme != "Default")
            writer.WriteString(nameof(s.Theme), s.Theme);
        if (s.ThemeSource != "Native")
            writer.WriteString(nameof(s.ThemeSource), s.ThemeSource);
        if (s.HasShownTips)
            writer.WriteBoolean(nameof(s.HasShownTips), true);
        if (s.LinuxSetupCompleted)
            writer.WriteBoolean(nameof(s.LinuxSetupCompleted), true);
        if (s.LinuxThemeInfoShown)
            writer.WriteBoolean(nameof(s.LinuxThemeInfoShown), true);
        if (s.WineMode != "System")
            writer.WriteString(nameof(s.WineMode), s.WineMode);
        if (s.WineVersion != "latest")
            writer.WriteString(nameof(s.WineVersion), s.WineVersion);
        if (!s.WineInstallFonts)
            writer.WriteBoolean(nameof(s.WineInstallFonts), false);
        if (!s.WineApplyTheme)
            writer.WriteBoolean(nameof(s.WineApplyTheme), false);
        if (s.ViewPresets.Count > 0)
        {
            writer.WritePropertyName(nameof(s.ViewPresets));
            JsonSerializer.Serialize(writer, s.ViewPresets, SettingsJsonContext.Default.DictionaryStringFilterViewSettings);
        }

        writer.WriteEndObject();
    }
}

public static class SettingsService
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "Data", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(
                           File.ReadAllText(FilePath),
                           SettingsJsonContext.Default.AppSettings)
                       ?? new AppSettings();
        }
        catch
        {
            /* corrupt file – fall back to defaults */
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Settings.SaveFailedFormat, FilePath, ex.Message));
        }
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(FilterViewSettings))]
[JsonSerializable(typeof(Dictionary<string, FilterViewSettings>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SettingsJsonContext : JsonSerializerContext;