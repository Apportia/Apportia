namespace Apportia.Services;

internal static class Log
{
    private static readonly string LogPath =
        Path.Combine(
            Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Apportia") + ".log");

    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            /* logging must never crash the app */
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(LogPath))
                File.Delete(LogPath);
        }
        catch
        {
            /* log file may not exist yet */
        }
    }
}