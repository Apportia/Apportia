namespace Apportia.Services;

/// Undoes Wine's host-system integration inside the active prefix.
/// - Removes `wine-extension-*.desktop` shortcuts in ~/.local/share/applications
/// that reference the Apportia prefix.
/// - Walks drive_c/users recursively and replaces every symlink (Wine seeds these to point
/// at $HOME/Music, $HOME/Templates, etc.) with an empty directory, without descending into
/// them, so apps can't reach out into the host home.
public static class WinePrefixSanitizer
{
    public static void Sanitize()
    {
        if (!OperatingSystem.IsLinux())
            return;
        RemoveDesktopFiles();
        ReplaceUserDirSymlinks();
    }

    private static void RemoveDesktopFiles()
    {
        var appsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications");
        if (!Directory.Exists(appsDir))
            return;
        var prefix = WineService.PrefixDir;
        foreach (var file in Directory.EnumerateFiles(appsDir, "wine-extension-*.desktop"))
        {
            try
            {
                if (File.ReadAllText(file).Contains(prefix, StringComparison.Ordinal))
                    File.Delete(file);
            }
            catch
            {
                // Cleanup must never abort the app launch — swallow IO races and permission errors.
            }
        }
    }

    private static void ReplaceUserDirSymlinks()
    {
        var usersDir = Path.Combine(WineService.PrefixDir, "drive_c", "users");
        if (!Directory.Exists(usersDir))
            return;
        foreach (var userDir in Directory.EnumerateDirectories(usersDir))
            WalkAndReplace(userDir);
    }

    private static void WalkAndReplace(string dir)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    File.Delete(entry);
                    Directory.CreateDirectory(entry);
                    continue;
                }

                if ((attrs & FileAttributes.Directory) != 0)
                    WalkAndReplace(entry);
            }
            catch
            {
                // Cleanup must never abort the app launch — swallow IO races and permission errors.
            }
        }
    }
}