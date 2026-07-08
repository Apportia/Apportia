using System.IO.Compression;

namespace Apportia.Services;

/// Fetches the bundled Windows fonts archive and unpacks it into Data/Linux/fonts.
/// The prefix seeder copies from that directory into drive_c/windows/Fonts on first launch.
public static class WineFontsClient
{
    private const string ArchiveUrl =
        "https://raw.githubusercontent.com/Apportia/Apportia/main/data/windows_fonts.zip";

    public static async Task EnsureDownloadedAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var fontsDir = WineService.FontsDir;
        if (Directory.Exists(fontsDir) && Directory.EnumerateFiles(fontsDir).Any())
            return;

        Directory.CreateDirectory(fontsDir);
        var tempArchive = Path.Combine(fontsDir, "windows_fonts.zip.tmp");
        try
        {
            if (!await GitHubClient.DownloadAssetAsync(ArchiveUrl, tempArchive, progress, ct))
                return;
            await ZipFile.ExtractToDirectoryAsync(tempArchive, fontsDir, true, ct);
        }
        catch
        {
            // Fonts are cosmetic — a failed fetch must not block the setup flow.
        }
        finally
        {
            try
            {
                File.Delete(tempArchive);
            }
            catch
            {
                // Temp file cleanup is best-effort; leaving a leftover doesn't affect correctness.
            }
        }
    }
}