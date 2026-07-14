namespace Apportia.Text;

/// Centralized user-facing string constants for the Apportia UI.
/// Nested classes group strings by role. Fields are sorted alphabetically within each class.
/// Fields with a <c>Format</c> suffix are <see cref="string.Format(string, object?)" />
/// patterns.
public static class UiText
{
    public static class Menu
    {
        // MainWindow context menu items
        public const string AddToQueue = "Add to Queue";
        public const string CancelInstallation = "Cancel Installation";
        public const string Install = "Install";
        public const string InstallAndRun = "Install & Run";
        public const string OpenFolder = "Open Folder";
        public const string Preview = "Preview";
        public const string Properties = "Properties";
        public const string RemoveFromQueue = "Remove from Queue";
        public const string Run = "Run";
        public const string RunAsAdministrator = "Run as Administrator";
        public const string RunWithParameters = "Run with Parameters...";
        public const string Settings = "Settings";
        public const string Terminate = "Terminate";
        public const string Uninstall = "Uninstall";
        public const string Update = "Update";
        public const string UpdateAndRun = "Update & Run";
        public const string VirusTotal = "VirusTotal...";
        public const string VisitWebsite = "Visit Website";
    }

    public static class Dialog
    {
        #region ChangelogDialog

        public const string ChangelogSubtitle = "Review the changes below before installing.";
        public const string ChangelogTitle = "Update Available";
        public const string ChangelogVersionFormat = "What's new in {0}";

        #endregion

        #region CopyProgressDialog

        public const string CopyImportCancelBody = "The import is not complete.\n\nCancel and delete the already copied files?";
        public const string CopyImportCancelTitle = "Cancel Import";
        public const string CopyImportHeader = "Importing App";
        public const string CopyProgressFilesFormat = "{0} of {1} files copied";
        public const string CopyProgressFinished = "All files copied.";
        public const string CopyProgressPreparing = "Preparing...";

        #endregion

        #region CustomAppWindow

        public const string CustomAppEditTitle = "App Settings";
        public const string CustomAppFolderPickerTitle = "Select App Folder";
        public const string CustomAppIconCurrentFormat = "Current icon ({0} x {1} px)";
        public const string CustomAppIconFileTypeName = "PNG Image";
        public const string CustomAppIconPickerTitle = "Select Icon (PNG)";
        public const string CustomAppIconSizeFormat = "{0} ({1} x {2} px)";
        public const string CustomAppImportTitle = "Import App";
        public const string CustomAppVersionUpdatedFormat = "Updated from stored version: {0}";
        public const string CustomAppVersionUpdatedNone = "none";

        #endregion

        #region ExePickerDialog

        public const string ExePickerPromptLine1 = "Multiple executables were found in the application folder.";
        public const string ExePickerPromptLine2 = "Select the main executable for this application:";
        public const string ExePickerTitle = "Select Executable";

        #endregion

        #region InstallOrchestrator

        public const string InstallDownloadFailedTitle = "Download Failed";
        public const string InstallExtractingFormat = "Extracting {0}...";
        public const string InstallFailedBodyFormat = "'{0}' was not installed correctly. No executable was found after installation. The incomplete installation has been removed.";
        public const string InstallFailedTitle = "Installation Failed";
        public const string InstallHashMismatchBody = "The downloaded file's hash does not match the expected value.\n\nThe file may be corrupted or tampered with.";
        public const string InstallHashMismatchTitle = "Hash Mismatch";
        public const string InstallHashProceedQuestion = "Do you want to proceed with the installation?";
        public const string InstallInstallingFormat = "Installing {0}...";
        public const string InstallJavaUnavailableBodyFormat = "{0} requires a Java runtime, but no Java plugins are available to install.";
        public const string InstallLaunchFailedTitle = "Launch Failed";
        public const string InstallPleaseWait = "Please wait";
        public const string InstallPreparingFormat = "Preparing {0}...";

        #endregion

        #region JavaRequiredDialog

        public const string JavaRequiredPromptLine1 = "This app requires a Java runtime to function.";
        public const string JavaRequiredPromptLine2 = "Select one or more Java plugins to add to the install queue:";
        public const string JavaRequiredTitle = "Java Required";

        #endregion

        #region LanguageDialog

        public const string LanguageEnglish = "English";
        public const string LanguagePromptLine1 = "This app is available in multiple languages.";
        public const string LanguagePromptLine2 = "Select your preferred language:";
        public const string LanguageTitle = "Select Language";

        #endregion

        #region LeftoverFilesDialog

        public const string LeftoverFilesBody = "The following items in the Apps folder do not belong there and may be leftovers from a failed or interrupted operation:";
        public const string LeftoverFilesTitle = "Leftover Files";
        public const string LeftoverKindFile = "File";
        public const string LeftoverKindFolder = "Folder";

        #endregion

        #region LinuxSetupDialog

        public const string LinuxDeleteBundledBody = "Switching to system Wine.\n\nThe bundled Wine installation and prefix in\n\n{0}\n\nare no longer needed. Delete them now?";
        public const string LinuxDeleteBundledTitle = "Delete bundled Wine files?";
        public const string LinuxDiskSpaceInsufficientFormat = "Not enough free disk space for the bundled Wine prefix. At least {0} GiB required, only {1:0.0} GiB available in {2}.";
        public const string LinuxDownloadFailed = "Download or extraction failed.";
        public const string LinuxDownloadingArchiveFormat = "Downloading {0}...";
        public const string LinuxDownloadingFonts = "Downloading Windows fonts...";
        public const string LinuxFetchingReleases = "Fetching Wine releases...";
        public const string LinuxFontsPromptBody = "Wine is installed. Optionally download the original Windows font pack for better rendering in Windows applications.\n\nYou can skip this and download it later.";
        public const string LinuxFontsPromptTitle = "Download Windows fonts?";
        public const string LinuxLatestHint = "\"latest\" automatically updates to the newest vanilla build.";
        public const string LinuxNoReleases = "No Wine release available. Check your connection and retry.";
        public const string LinuxPrefixCreateFailedFormat = "Failed to create prefix directory '{0}': {1}";
        public const string LinuxPrefixLocationFailedFormat = "No suitable location for the bundled Wine prefix. Install drive filesystem is unsupported and /tmp fallback needs 5 GiB free (has {0:0.0} GiB).";
        public const string LinuxPromptLine1 = "Wine lets Apportia run Windows applications on Linux.";
        public const string LinuxPromptLine2 = "Choose how to provide it.";
        public const string LinuxReleasesFetchFailedFormat = "Could not fetch Wine releases: {0}";
        public const string LinuxSetupFailedFormat = "Setup failed: {0}";
        public const string LinuxSetupTitle = "Wine Setup";
        public const string LinuxSystemModeInstallation = "Installation:";
        public const string LinuxSystemModeSystem = "Use system Wine and ~/.wine";
        public const string LinuxSystemModeBundled = "Use bundled Wine in Data/Linux (isolated)";
        public const string LinuxVersionHeader = "Bundled Wine version:";

        #endregion

        #region MirrorDialog

        public const string MirrorFailedGeneric = "The download failed.\n\nSelect a mirror to retry:";
        public const string MirrorFailedNamedFormat = "The download from {0} failed.\n\nSelect a different mirror to retry:";
        public const string MirrorTitle = "Connection Timed Out";

        #endregion

        #region RunArgsDialog

        public const string RunArgsFilePickerTitle = "Select File(s)";
        public const string RunArgsFolderPickerTitle = "Select Folder(s)";
        public const string RunArgsParameters = "Parameters";
        public const string RunArgsTitle = "Run App";

        #endregion

        #region SaveViewDialog

        public const string SaveViewFilterAll = "All Apps";
        public const string SaveViewFilterInstalled = "Installed";
        public const string SaveViewFilterNotInstalled = "Not Installed";
        public const string SaveViewPrompt = "Save current view settings for:";
        public const string SaveViewTitle = "Save View";

        #endregion

        #region SecurityNoticeDialog

        public const string SecurityAlternativesHeader = "ALTERNATIVES";
        public const string SecurityTitle = "Security Notice";
        public const string SecurityVerifiedFormat = "Last verified {0}";

        #endregion

        #region TerminateDialog

        public const string TerminateAllAppsName = "Running Apps";
        public const string TerminateTitle = "Terminate";
        public const string TerminateTitleFormat = "Terminate {0}";
        public const string TerminateTotalsFormat = "Total: {0} CPU \u00b7 {1} RAM \u00b7 Free: {2}";

        #endregion

        #region TipsDialog

        public const string TipsHeader = "Tips & Shortcuts";
        public const string TipsSubtitle = "Everything you need to get the most out of Apportia.";
        public const string TipsTitle = "Tips & Shortcuts";

        // Tip cards
        public const string Tip01Body = "Press Ctrl+F from anywhere to focus the search box instantly.";
        public const string Tip01Title = "Ctrl+F \u2014 Jump to Search";
        public const string Tip02Body = "Select an uninstalled app and press Ctrl+Enter to install it immediately, skipping any confirmation dialog.";
        public const string Tip02Title = "Ctrl+Enter \u2014 Install Without Dialog";
        public const string Tip03Body = "Hold Ctrl while clicking an uninstalled app to install it directly or add it to the queue without any prompts. Perfect for installing multiple apps in quick succession.";
        public const string Tip03Title = "Ctrl+Click \u2014 Install Without Dialog";
        public const string Tip04Body = "Pass command-line arguments to apps via Run with Parameters... in the context menu. You can also launch Apportia with a file path to open it directly with a supported app.";
        public const string Tip04Title = "CLI Arguments \u2014 Open Files with Any App";
        public const string Tip05Body = "Right-click any app and choose VirusTotal... to scan it by hash or upload it directly. Results appear inline without opening a browser.";
        public const string Tip05Title = "VirusTotal \u2014 Scan Before You Run";
        public const string Tip06Body = "Save your current view settings as a preset via the save button. Presets are restored automatically when switching between All, Installed, and Not Installed.";
        public const string Tip06Title = "View Presets \u2014 Save Your Layout";
        public const string Tip07Body = "Right-click any app and choose Preview to view a screenshot before installing.";
        public const string Tip07Title = "Preview \u2014 See a Screenshot";
        public const string Tip08Body = "Click any column header to sort. The disk usage column shows actual space used per installed app, great for finding what's taking up space.";
        public const string Tip08Title = "Sortable Columns \u2014 Including Disk Usage";
        public const string Tip09Body = "Right-click any app and choose Properties to inspect metadata not shown in the list: download URL, file hash, install path, and more.";
        public const string Tip09Title = "Properties \u2014 Full App Metadata";
        public const string Tip10Body = "Use the Import App button to integrate portable apps from any source into Apportia. Apps are added as-is; Apportia does not make them portable.";
        public const string Tip10Title = "Custom App Import";
        public const string Tip11Body = "Before uninstalling, you can optionally back up an app's data. Reinstall and restore it later with everything intact, perfect for using apps on demand without losing your settings.";
        public const string Tip11Title = "Backup & Restore App Data";
        public const string Tip12Body = "If Apportia lives at a fixed location, use the included setup scripts to add it to your file manager's context menu and open files directly from there.";
        public const string Tip12Title = "System Integration \u2014 Context Menu";

        #endregion

        #region MainWindow

        public const string MainAddCustomAppFailed = "Add App Failed";
        public const string MainAddToQueueFormat = "An installation is already in progress.\n\nAdd {0} to the queue to {1} it afterward?";
        public const string MainAppRunningTitle = "App is Running";
        public const string MainAppRunningBody = "The app is currently running. It must be closed before it can be uninstalled.\n\nForce quit now?";
        public const string MainAppRunningProcessesFormat = "{0} has running processes:\n\n{1}\n\nForce-quit them to proceed?";
        public const string MainBackupExistsBodyFormat = "A backup of {0}'s data already exists.\n\nWhich backup do you want to keep?";
        public const string MainCancelInstallActiveFormat = "{0} is currently being installed.\n\n";
        public const string MainArgsUpdatedTitle = "Arguments Updated";
        public const string MainBackupAlreadyExistsBody = "A backup already exists.\n\nKeep the new backup or the existing one?";
        public const string MainBackupAlreadyExistsTitle = "Backup Already Exists";
        public const string MainBackupUserDataTitle = "Backup User Data";
        public const string MainBackupUserDataBodyFormat = "Do you want to save a backup of your {0} data before uninstalling?";
        public const string MainCancelInstallBody = "Canceling now may leave the application in a corrupt state.\n\nAre you sure you want to cancel?";
        public const string MainCancelInstallInProgressFormat = "{0} is currently being downloaded.\n\nWould you like to cancel the installation?";
        public const string MainCategoryAdvanced = "Advanced";
        public const string MainCategoryGames = "Games";
        public const string MainCloseInProgressActiveFormat = "{0} is currently being installed.\n\n";
        public const string MainCloseInProgressBody = "Closing now will abort the installation and may leave it in a corrupt state.\n\nAre you sure you want to close?";
        public const string MainCloseInProgressCurrentApp = "the current app";
        public const string MainCloseInProgressTitle = "Installation in Progress";
        public const string MainDeleteFolderBodyFormat = "Permanently delete \"{0}\" and everything inside it?";
        public const string MainDeleteFolderTitle = "Delete Folder";
        public const string MainInstallInProgressTitle = "Installation Running";
        public const string MainInstallPromptFormat = "Would you like to install {0}?";
        public const string MainInstallPromptSizeSuffixFormat = "\n\nRequired:   {0}\nAvailable:  {1}\n\nRequired size covers the app only.\nUser data created later is not counted.";
        public const string MainNoPreviewBodyFormat = "No preview available for {0}.";
        public const string MainNoPreviewTitle = "No Preview";
        public const string MainNotEnoughSpaceBody = "Not enough disk space to install {0}.\n\nRequired:   {1}\nAvailable:  {2}\n\nFree up disk space and click Retry, or Cancel to abort.";
        public const string MainNotEnoughSpaceTitleFormat = "{0} \u2014 Not Enough Space";
        public const string MainQueuedRemoveFormat = "{0} is currently in the installation queue.\n\nWould you like to remove it?";
        public const string MainSecurityNoticeTitleFormat = "Security Notice \u2014 {0}";
        public const string MainUninstallFailedTitle = "Uninstall Failed";
        public const string MainUninstallJavaExtraFormat = "Remove {0} and all its data?\n\nThis is the last app requiring Java.\n\nThe following Java plugins will also be uninstalled:\n{1}";
        public const string MainUninstallSimpleFormat = "Remove {0} and all its data?";
        public const string MainUninstallTitle = "Uninstall";
        public const string MainUnknownAppFolderBodyFormat = "Found an app folder that isn't registered in the app database:\n\n{0}\n\nWhat should Apportia do with it?";
        public const string MainUnknownAppFolderTitle = "Unknown App Folder";
        public const string MainUpdateAvailableFormat = "Update Available \u2014 {0}";
        public const string MainUpdateAvailableBodyFormat = "A newer version of {0} is available.\n\nWould you like to update now?";
        public const string MainUpdateFailedTitle = "Update Failed";
        public const string MainVerbInstall = "install";
        public const string MainVerbUpdate = "update";
        public const string MainIpcArgsBodyFormat = "A second instance was launched with new CLI arguments:\n\n{0}\n\nThese have replaced the current arguments.";
        public const string MainIpcArgsMoreFormat = "\n... and {0} more";

        #endregion

        #region VirusTotalDialog

        public const string VtAnalysisCancelled = "Cancelled.";
        public const string VtAnalysisTimedOut = "Analysis timed out after 5 minutes.";
        public const string VtHttpErrorFormat = "HTTP {0}: {1}";
        public const string VtAlertHashError = "Hash Error";
        public const string VtAlertHashFailedFormat = "Failed to read '{0}':\n{1}";
        public const string VtAlertNoApiKey = "Please enter an API key.";
        public const string VtAlertNoFileSelected = "No file selected.";
        public const string VtAlertNoResults = "No results returned.";
        public const string VtAlertNoResultsAfter = "No results returned after analysis.";
        public const string VtAlertSaveApiKeyNoKey = "No API key entered.";
        public const string VtAlertScanError = "Scan Error";
        public const string VtAlertSubdirScanFailedFormat = "Failed to scan subdirectories:\n{0}";
        public const string VtAlertUnexpectedError = "Unexpected Error";
        public const string VtAlertUploadNoId = "No analysis ID returned.";
        public const string VtAlertVtError = "VirusTotal Error";
        public const string VtAnalysisError = "Analysis Error";
        public const string VtComputingHash = "Computing file hash...";
        public const string VtDetectionBadgeFormat = "{0}/{1}";
        public const string VtDetectionNone = "no threats detected";
        public const string VtDetectionSome = "engines detected a threat";
        public const string VtDirectoryScanError = "Directory Scan Error";
        public const string VtNoBinariesFound = "No binary files found in the app directory.";
        public const string VtNoDownloadInfo = "No download file information available for this app.";
        public const string VtNotSubmitted = "This file has not been submitted to VirusTotal yet.";
        public const string VtNotYetScanned = "Not yet scanned. Click Scan to query VirusTotal.";
        public const string VtOpenWebsite = "Open on VirusTotal.com";
        public const string VtQuerying = "Querying VirusTotal...";
        public const string VtScanDateCachedFormat = "Cached {0:yyyy-MM-dd} (outdated)";
        public const string VtScanDateFormat = "Scanned {0:yyyy-MM-dd}";
        public const string VtSaveApiKeyBodyFormat = "How should the API key be saved?\n\nPermanent storage writes the key unencrypted to:\n{0}";
        public const string VtSaveApiKeyTitle = "Save API Key";
        public const string VtSectionCommunity = "Community";
        public const string VtSectionEngineResults = "Engine Results";
        public const string VtSectionFileInfo = "File Info";
        public const string VtSectionHashes = "Hashes";
        public const string VtSectionSandbox = "Sandbox Verdicts";
        public const string VtSectionSignature = "Signature";
        public const string VtSectionSubmission = "Submission";
        public const string VtSectionTrid = "File Type Analysis (TrID)";
        public const string VtSubmissionUploadError = "Upload Error";
        public const string VtTitle = "VirusTotal Analysis";
        public const string VtTitleFormat = "VirusTotal \u2014 {0}";
        public const string VtUploadAnalyzingFormat = "File uploaded. VirusTotal is analyzing across all engines... ({0}s elapsed)";
        public const string VtUploadFetching = "Analysis complete. Fetching full report...";
        public const string VtUploadNotFoundInstalled = "File not found in VirusTotal database. Click 'Upload & Scan' to submit it.";
        public const string VtUploadTooLargeBody = "File exceeds the 32 MB upload limit for VirusTotal.";
        public const string VtUploadingFormat = "Uploading \u2018{0}\u2019 to VirusTotal...";

        #endregion
    }

    public static class Error
    {
        public const string CustomAppEnterName = "Please enter a name.";
        public const string CustomAppSelectExe = "Please select an executable.";
        public const string CustomAppSelectFolder = "Please select an app folder.";
        public const string CustomAppSelectIcon = "Please select an icon.";
    }

    public static class Tip
    {
        // MainWindow toolbar tooltips
        public const string CategoryDisplay = "Category Display";
        public const string CategoryScope = "Category Scope";
        public const string ElevatedProcess = "Runs as Administrator";
        public const string FontSize = "Font Size";
        public const string IconSize = "Icon Size";
        public const string ImportCustomApp = "Import Custom App";
        public const string InstallFilter = "Install Filter";
        public const string SaveViewPreset = "Save View Preset";
        public const string TerminateAllApps = "Terminate Running Apps";
        public const string Tips = "Tips";
        public const string ToggleTheme = "Toggle Theme";
        public const string UpdateApportia = "Update Apportia";
        public const string UpdateAllApps = "Update all apps with pending updates";
        public const string ViewMode = "View Mode";
        public const string WineSetup = "Wine Setup";
    }

    public static class Column
    {
        // AppProperties columns
        public const string LanguageFile = "File";
        public const string LanguageHash = "Hash";
        public const string LanguageLanguage = "Language";

        // MainWindow columns
        public const string MainDescription = "Description";

        // TerminateDialog columns
        public const string TerminateCommand = "Command";
        public const string TerminateCpu = "CPU";
        public const string TerminateExecutable = "Executable";
        public const string TerminatePid = "PID";
        public const string TerminateRam = "RAM";
        public const string TerminateStarted = "Started";

        // VirusTotal columns
        public const string VtCategory = "Category";
        public const string VtClassification = "Classification";
        public const string VtEngine = "Engine";
        public const string VtFile = "File";
        public const string VtFileType = "File Type";
        public const string VtMatchPercent = "Match %";
        public const string VtResult = "Result";
        public const string VtSandbox = "Sandbox";
    }

    public static class Header
    {
        // AppProperties section headers
        public const string PropsDownload = "DOWNLOAD";
        public const string PropsGeneral = "GENERAL";
        public const string PropsInstallation = "INSTALLATION";
        public const string PropsLanguages = "LANGUAGES";
        public const string PropsVersion = "VERSION";

        // CustomAppWindow field labels
        public const string CustomCategory = "Category";
        public const string CustomDescription = "Description";
        public const string CustomExecutable = "Executable";
        public const string CustomFolder = "App Folder";
        public const string CustomIcon = "Icon";
        public const string CustomIconSource = "Icon Source";
        public const string CustomName = "Name";
        public const string CustomSubCategory = "SubCategory";
        public const string CustomVersion = "Version";
        public const string CustomVersionSource = "Version Source";
        public const string CustomWebsite = "Website";

        // AppProperties entries (labels)
        public const string PropsAvailable = "Available";
        public const string PropsCategory = "Category";
        public const string PropsClass = "Class";
        public const string PropsClassAdvanced = "Advanced";
        public const string PropsClassLegacy = "Legacy";
        public const string PropsCopy = "Copy";
        public const string PropsDescription = "Description";
        public const string PropsDisplayVersion = "Display Version";
        public const string PropsDownloadFile = "Download File";
        public const string PropsDownloadPath = "Download Path";
        public const string PropsDownloadSize = "Download Size";
        public const string PropsHash = "Hash";
        public const string PropsInstallLocation = "Install Location";
        public const string PropsInstallSize = "Install Size";
        public const string PropsInstalledFormat = "{0} (App: {1}, Data: {2})";
        public const string PropsJoinedDate = "Joined Date";
        public const string PropsLanguage = "Language";
        public const string PropsName = "Name";
        public const string PropsPackageVersion = "Package Version";
        public const string PropsRequiresJava = "Requires Java";
        public const string PropsSection = "Section";
        public const string PropsSubCategory = "Sub-Category";
        public const string PropsTitle = "Properties";
        public const string PropsTitleFormat = "{0} ({1})";
        public const string PropsUpdateDate = "Update Date";
        public const string PropsUsedSize = "Used Size";
        public const string PropsUserAgent = "User Agent";
        public const string PropsWebsite = "Website";
        public const string PropsYes = "Yes";

        // VirusTotal labels
        public const string VtApiKey = "API Key";
        public const string VtFile = "File";

        // Relative-date labels (AppProperties)
        public const string RelDaysAgoFormat = "{0}, {1} days ago";
        public const string RelToday = "Today";
        public const string RelWeekAgo = "1 week ago";
        public const string RelYesterday = "Yesterday";
    }

    public static class Button
    {
        // Shared dialog buttons
        public const string AddFile = "Add File...";
        public const string AddFolder = "Add Folder...";
        public const string AddParameter = "Add Parameter";
        public const string AddToQueue = "Add to Queue";
        public const string Browse = "Browse...";
        public const string Cancel = "Cancel";
        public const string CancelImportNo = "No, Continue";
        public const string CancelImportYes = "Yes, Cancel";
        public const string Close = "Close";
        public const string Copy = "Copy";
        public const string CustomAppImport = "Import";
        public const string CustomAppSave = "Save";
        public const string DeleteAll = "Delete All";
        public const string GotIt = "Got it!";
        public const string Install = "Install";
        public const string InstallFonts = "Install Fonts";
        public const string InstallUpdate = "Install Update";
        public const string Ok = "OK";
        public const string Proceed = "Proceed";
        public const string ProceedAnyway = "Proceed Anyway";
        public const string RemoveArg = "x";
        public const string Reset = "Reset";
        public const string Retry = "Retry";
        public const string Run = "Run";
        public const string RunAsAdministrator = "Run as Administrator";
        public const string RunWithoutParameters = "Run without Parameters";
        public const string Save = "Save";
        public const string Scan = "Scan";
        public const string ScanWithVirusTotal = "Scan with VirusTotal";
        public const string Select = "Select";
        public const string Terminate = "Terminate";
        public const string TerminateAll = "Terminate All";
        public const string Update = "Update";
        public const string UploadAndScan = "Upload & Scan";
        public const string VtRescan = "Rescan";
        public const string VtSavePermanent = "Save Permanently";
        public const string VtSessionOnly = "Session Only";
        public const string WineDelete = "Delete";
        public const string WineDownload = "Download";
        public const string WineKeep = "Keep";
        public const string WineSkip = "Skip";

        // MainWindow-specific button labels
        public const string CancelInstallation = "Cancel Installation";
        public const string CloseAnyway = "Close Anyway";
        public const string Delete = "Delete";
        public const string ForceQuitUninstall = "Force Quit & Uninstall";
        public const string InstallAndRun = "Install & Run";
        public const string KeepExisting = "Keep Existing";
        public const string KeepNew = "Keep New";
        public const string KeepRunning = "Keep Running";
        public const string MoveToCustomApps = "Move to CustomApps";
        public const string RemoveFromQueue = "Remove from Queue";
        public const string SaveBackup = "Save Backup";
        public const string Skip = "Skip";
        public const string Uninstall = "Uninstall";
        public const string UpdateAndRun = "Update & Run";
    }

    public static class Placeholder
    {
        public const string CustomAppIconPath = "Pick from above or browse...";
        public const string VtApiKey = "Enter your VirusTotal API key";
    }

    public static class Status
    {
        // Toolbar labels
        public const string CategoryDisplayCategories = "Categories";
        public const string CategoryDisplayNoGroups = "No Groups";
        public const string CategoryDisplayTree = "Tree";
        public const string CategoryScopeExtended = "Extended";
        public const string CategoryScopeFull = "Full";
        public const string CategoryScopeStandard = "Standard";
        public const string FontSizeFormat = "{0}pt";
        public const string IconSizeFormat = "{0}px";
        public const string InstallFilterAll = "All Apps";
        public const string InstallFilterInstalled = "Installed";
        public const string InstallFilterNotInstalled = "Not Installed";
        public const string ViewModeGrid = "Grid";
        public const string ViewModeList = "List";

        // Download bar
        public const string DownloadingUpdateFormat = "Downloading update {0}...";
        public const string DownloadingUpdateProgressFormat = "Downloading update {0}... {1}%";
        public const string UpdateVersionFormat = "Update {0}";
        public const string UpdateAll = "Update All";
    }
}
