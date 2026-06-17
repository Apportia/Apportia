using System.IO.Pipes;
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
            if (args.Length > 0)
                TrySendArgs(args);
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

    private static void TrySendArgs(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "Apportia", PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(string.Join("\0", args));
            writer.Flush();
        }
        catch (Exception)
        {
            /* best-effort: main instance may not be ready yet */
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
