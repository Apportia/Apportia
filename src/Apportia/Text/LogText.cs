namespace Apportia.Text;

/// Centralized log-message string constants for the Apportia.
/// Nested classes group strings by originating service or view. Fields are
/// sorted alphabetically within each class. Fields with a <c>Format</c>
/// suffix are <see cref="string.Format(string, object?)" /> patterns.
public static class LogText
{
    public static class AppImage
    {
        public const string IconCorruptFormat = "Icon corrupt, discarding: {0} @ {1} - {2}";
        public const string IconNotFoundFormat = "Icon not found: {0} ({1})";
        public const string IconWriteFailedFormat = "Icon write failed: {0} - {1}";
        public const string LoadSvgFailedFormat = "Failed to load SVG: {0}";
        public const string PreviewNotFoundFormat = "Preview not found: {0} ({1})";
        public const string PreviewWriteFailedFormat = "Preview write failed: {0} - {1}";
    }

    public static class Custom
    {
        public const string DatabaseWriteVerificationFailedFormat = "Custom app database write verification failed: {0}";
        public const string FolderPickerFailedFormat = "Custom app folder picker failed: {0}";
        public const string IconPickerFailedFormat = "Icon picker failed: {0}";
        public const string PopulateFromFolderFailedFormat = "PopulateFromFolderAsync failed: {0}";
        public const string CancelCleanupFailedFormat = "Failed to remove cancelled import folder '{0}': {1}";
    }

    public static class DiskUsage
    {
        public const string DiskSpaceCheckFailedFormat = "Disk space check failed for '{0}': {1}";
    }

    public static class CurrentApp
    {
        public const string SaveDatabaseFailedFormat = "Failed to save current_app_database.json: {0}";
    }

    public static class Install
    {
        public const string BackupRestoreFailedFormat = "Failed to restore backup for '{0}': {1}";
        public const string ConnectionTimedOutFormat = "Connection to {0} timed out.";
        public const string DownloadFailedFormat = "Download failed for '{0}': {1}";
        public const string DownloadUrlFailedFormat = "Failed to download {0}";
        public const string HashMismatchFormat = "Hash verification failed for '{0}'";
        public const string LaunchFailedFormat = "Launch failed for '{0}': {1}";
        public const string SevenZipStartFailed = "Failed to start 7z process";
    }

    public static class Leftover
    {
        public const string DeleteFailedFormat = "Failed to delete leftover '{0}': {1}";
    }

    public static class Main
    {
        public const string AddCustomAppFailedFormat = "Add custom app failed: {0}";
        public const string CloseConfirmationFailedFormat = "Close confirmation failed, forcing shutdown: {0}";
        public const string CustomAppUpdateFailedFormat = "Custom app update failed for '{0}': {1}";
        public const string ImportSourceDeleteFailedFormat = "Import copied '{0}' but the original folder could not be deleted: {1}";
        public const string ImportUnknownAsCustomFailedFormat = "ImportUnknownAsCustomAsync failed: {0}";
        public const string InstallFilterCycleFailedFormat = "OnInstallFilterCycle failed: {0}";
        public const string InstallFilterPointerReleasedFailedFormat = "OnInstallFilterPointerReleased failed: {0}";
        public const string MenuAddImportDialogFailedFormat = "OnMenuAdd import dialog failed: {0}";
        public const string MenuCancelInstallFailedFormat = "OnMenuCancelInstall failed: {0}";
        public const string MenuRunFailedFormat = "OnMenuRun failed: {0}";
        public const string MenuRunWithArgsFailedFormat = "OnMenuRunWithArgs failed: {0}";
        public const string MenuSettingsCustomAppEditFailedFormat = "OnMenuSettings custom app edit failed: {0}";
        public const string MenuTerminateFailedFormat = "OnMenuTerminate failed: {0}";
        public const string MenuUninstallConfirmationFailedFormat = "OnMenuUninstall confirmation failed: {0}";
        public const string RowTappedActivationFailedFormat = "OnAppRowTapped activation failed: {0}";
        public const string SaveViewFailedFormat = "OnSaveView failed: {0}";
        public const string SelfUpdateFailedFormat = "Self-update failed: {0}";
        public const string SelfUpdateSetupFailedFormat = "Self-update setup failed: {0}";
        public const string UninstallFailedFormat = "Uninstall failed for '{0}': {1}";
    }

    public static class Settings
    {
        public const string SaveFailedFormat = "Failed to save settings to '{0}': {1}";
    }

    public static class Update
    {
        public const string SkippedEntryOutsideExtractionFormat = "Skipped update entry outside extraction directory: '{0}'";
    }

    public static class VirusTotal
    {
        public const string DirectoryScanFailedFormat = "Directory scan failed: {0}";
        public const string HashReadFailedFormat = "Hash read failed for '{0}': {1}";
        public const string LookupFailedFormat = "VirusTotal lookup failed for '{0}': {1}";
        public const string UploadFailedFormat = "VirusTotal upload failed for '{0}': {1}";
    }

    public static class Wine
    {
        public const string LaunchFailedFormat = "Failed to launch Wine ('{0}') to initialize the prefix at '{1}'.";
        public const string LinuxSetupFailedFormat = "Linux setup failed: {0}";
        public const string PrefixCreateFailedFormat = "Failed to create prefix directory '{0}': {1}";
        public const string PrefixInitTimedOutFormat = "Wine prefix initialization timed out at '{0}'.";
        public const string PrefixInitUnknownFailureFormat = "Wine prefix at '{0}' could not be initialized. Check your Wine installation.";
        public const string PrefixMissingSharedLibrariesHeader = "Wine could not start because these shared libraries are missing:";
        public const string ReleasesFetchFailedFormat = "Could not fetch Wine releases: {0}";
        public const string RunnerDownloadFailedFormat = "Wine runner download failed: {0}";
        public const string WineNotAvailable = "Wine is not available. Install Wine or configure a bundled runner.";
    }
}
