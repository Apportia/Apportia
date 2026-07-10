using System.Text;

namespace Apportia.Services;

public static class AtomicFile
{
    private const string TempSuffix = ".tmp";

    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        var tmp = path + TempSuffix;
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static async Task WriteAllTextAsync(string path, string contents, Encoding? encoding = null, CancellationToken ct = default)
    {
        var tmp = path + TempSuffix;
        try
        {
            if (encoding != null)
                await File.WriteAllTextAsync(tmp, contents, encoding, ct);
            else
                await File.WriteAllTextAsync(tmp, contents, ct);
            File.Move(tmp, path, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + TempSuffix;
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static void SweepStaleTempFiles(string rootDir, TimeSpan minAge)
    {
        if (!Directory.Exists(rootDir))
            return;
        var cutoff = DateTime.UtcNow - minAge;
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, "*" + TempSuffix, SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > cutoff)
                        continue;
                }
                catch
                {
                    continue;
                }

                TryDelete(file);
            }
        }
        catch
        {
            /* sweep is best-effort; residual .tmp files are harmless */
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* nothing to do — leftover .tmp will be swept on next launch */
        }
    }
}