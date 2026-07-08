namespace Apportia.Services;

/// One-shot copy of Data/Linux/fonts into drive_c/windows/Fonts of a freshly created prefix.
/// Guarded by a marker file so subsequent launches don't re-clobber the prefix.
public static class WinePrefixFonts
{
    private const string MarkerFile = ".apportia-fonts-applied";

    public static void Apply()
    {
        var fonts = WineService.FontsDir;
        if (!Directory.Exists(fonts) || !Directory.EnumerateFiles(fonts).Any())
            return;
        var prefix = WineService.PrefixDir;
        var marker = Path.Combine(prefix, MarkerFile);
        if (File.Exists(marker))
            return;

        var target = Path.Combine(prefix, "drive_c", "windows", "Fonts");
        Directory.CreateDirectory(target);
        CopyTree(fonts, target);
        File.WriteAllText(marker, string.Empty);
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyTree(dir, Path.Combine(dst, Path.GetFileName(dir)));
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
    }
}
