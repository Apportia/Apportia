namespace Apportia.Services;

public static class AppBackupService
{
    private static string BackupDir =>
        Path.Combine(AppContext.BaseDirectory, "Data", "AppBackups");

    private static string GetBackupDataDir(string sectionName)
    {
        return Path.Combine(BackupDir, sectionName, "Data");
    }

    public static bool HasBackup(string sectionName)
    {
        var dir = GetBackupDataDir(sectionName);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    public static void MoveToBackup(string sourceDataDir, string sectionName)
    {
        var backupDataDir = GetBackupDataDir(sectionName);
        Directory.CreateDirectory(Path.GetDirectoryName(backupDataDir)!);
        Directory.Move(sourceDataDir, backupDataDir);
    }

    public static void DeleteBackup(string sectionName)
    {
        var dir = Path.Combine(BackupDir, sectionName);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    public static void RestoreBackup(string sectionName, string installDir)
    {
        var backupDataDir = GetBackupDataDir(sectionName);
        if (!Directory.Exists(backupDataDir))
            return;

        var targetDataDir = Path.Combine(installDir, "Data");
        if (Directory.Exists(targetDataDir))
            Directory.Delete(targetDataDir, true);

        Directory.CreateDirectory(installDir);
        Directory.Move(backupDataDir, targetDataDir);

        CleanupEmptyFolders();
    }

    private static void CleanupEmptyFolders()
    {
        if (!Directory.Exists(BackupDir))
            return;

        foreach (var dir in Directory.GetDirectories(BackupDir))
        {
            if (!Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(dir, true);
        }
    }
}