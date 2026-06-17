using Avalonia;

namespace Apportia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "Apportia.lock");
        FileStream? lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            /* another instance already holds the lock */
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            lockFile.Dispose();
        }
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
                               .UsePlatformDetect()
                               .WithInterFont();
#if DEBUG
        builder = builder.LogToTrace();
#endif
        return builder;
    }
}