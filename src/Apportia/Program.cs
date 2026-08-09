using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Apportia.Platform;
using Avalonia;

namespace Apportia;

internal static class Program
{
    internal static readonly string PipeName = "Apportia." + ComputeInstanceId();

    [STAThread]
    public static void Main(string[] args)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(dataDir);
        var lockPath = Path.Combine(dataDir, ".lock");
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
                lockFile.Lock(0, 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* another instance from this install directory already holds the lock */
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
            Win32Window.AllowAnyForeground();
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
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

    private static string ComputeInstanceId()
    {
        var path = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
            path = path.ToLowerInvariant();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash, 0, 8);
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