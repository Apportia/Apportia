using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed class AppSettings
{
    public CategoryDisplayMode CategoryDisplay { get; set; } = CategoryDisplayMode.Full;
    public InstallFilter InstallFilter { get; set; } = InstallFilter.All;
    public CategoryScope CategoryScope { get; set; } = CategoryScope.Standard;
    public string SortColumn { get; set; } = "Name";
    public bool SortDescending { get; set; }
    public double ColumnName { get; set; } = 200;
    public double ColumnVersion { get; set; } = 90;
    public double ColumnDownload { get; set; } = 85;
    public double ColumnInstall { get; set; } = 80;
    public double ColumnReleased { get; set; } = 90;
    public double ColumnUpdated { get; set; } = 90;
    public double ColumnUsed { get; set; } = 75;
    public bool IsGridView { get; set; }
    public string Theme { get; set; } = "Default";
    public double WindowWidth { get; set; } = 1024;
    public double WindowHeight { get; set; } = 720;
    public int IconSize { get; set; } = 24;
    public int FontSize { get; set; } = 13;
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
            Log($"Failed to save settings to '{FilePath}': {ex.Message}");
        }
    }

    public static void ClearLog()
    {
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Apportia");
            var logPath = Path.Combine("/tmp", exeName + ".log");
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
        catch
        {
            /* log directory may not exist yet */
        }
    }

    internal static void Log(string message)
    {
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Apportia");
            File.AppendAllText(
                Path.Combine("/tmp", exeName + ".log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            /* logging must never crash the app */
        }
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SettingsJsonContext : JsonSerializerContext;
