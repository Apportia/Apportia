using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Apportia.Ui;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Apportia.Views;

public partial class LinuxSetupDialog : Window
{
    private const string LatestKey = "latest";
    private const long RequiredDiskGib = 2;
    private const long RequiredDiskGibBytes = RequiredDiskGib * 1024L * 1024L * 1024L;

    private const string SaveLabel = UiText.Button.Save;
    private const string UpdateLabel = UiText.Button.Update;

    private CancellationTokenSource? _downloadCts;
    private string _initialMode = string.Empty;
    private string _initialVersion = string.Empty;
    private IReadOnlyList<WineRunnerRelease> _releases = [];
    private bool _releasesRequested;
    private bool _updateAvailable;

    public LinuxSetupDialog()
    {
        InitializeComponent();
        WindowAutoRecenter.Attach(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);

        var settings = SettingsService.Load();
        _initialMode = settings.WineMode;
        _initialVersion = settings.WineVersion;
        var systemAvailable = WineService.IsSystemWineAvailable();

        SystemWinePanel.IsVisible = systemAvailable;
        SystemWineMissingWarning.IsVisible = !systemAvailable;
        var bundled = settings.WineMode.Equals("Bundled", StringComparison.OrdinalIgnoreCase);
        if (systemAvailable)
        {
            SystemModeRadio.IsChecked = !bundled;
            BundledModeRadio.IsChecked = bundled;
        }
        else
        {
            BundledModeRadio.IsChecked = true;
        }

        InstallFontsPill.IsChecked = settings.WineInstallFonts;
        ApplyThemePill.IsChecked = settings.WineApplyTheme;

        UpdateVersionPanelVisibility();
        EnsureReleasesLoaded();
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        UpdateVersionPanelVisibility();
        EnsureReleasesLoaded();
        RefreshSaveButtonLabel();
    }

    private void EnsureReleasesLoaded()
    {
        if (_releasesRequested || BundledModeRadio.IsChecked != true)
            return;
        _releasesRequested = true;
        _ = LoadReleasesAsync();
    }

    private void OnVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshSaveButtonLabel();
    }

    private void RefreshSaveButtonLabel()
    {
        var showUpdate = IsInitialConfigUnchanged() && _updateAvailable;
        SaveButton.Content = showUpdate ? UpdateLabel : SaveLabel;
        SaveButton.Classes.Set("update", showUpdate);
    }

    private bool IsInitialConfigUnchanged()
    {
        var settings = SettingsService.Load();
        if (!settings.LinuxSetupCompleted)
            return false;
        if (BundledModeRadio.IsChecked != true)
            return false;
        if (!_initialMode.Equals("Bundled", StringComparison.OrdinalIgnoreCase))
            return false;
        var picked = VersionCombo.SelectedItem as string;
        return picked is not null && picked.Equals(_initialVersion, StringComparison.OrdinalIgnoreCase);
    }

    private void CheckForUpdate()
    {
        _updateAvailable = false;
        if (!_initialVersion.Equals(LatestKey, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_initialMode.Equals("Bundled", StringComparison.OrdinalIgnoreCase))
            return;
        var latest = WineRunnersClient.PickRelease(_releases, LatestKey);
        if (latest is null)
            return;
        var installed = WineService.ResolveActiveRunnerDir();
        if (installed is null)
            return;
        var installedName = Path.GetFileName(installed.TrimEnd(Path.DirectorySeparatorChar));
        var latestName = WineRunnersClient.StripExtension(latest.ArchiveName);
        _updateAvailable = !installedName.Equals(latestName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FontsInstalled()
    {
        return Directory.Exists(WineService.FontsDir)
               && Directory.EnumerateFiles(WineService.FontsDir).Any();
    }

    private void UpdateVersionPanelVisibility()
    {
        var showBundled = BundledModeRadio.IsChecked == true;
        VersionPanel.IsVisible = showBundled;
    }

    private async Task LoadReleasesAsync()
    {
        ErrorText.IsVisible = false;
        RetryButton.IsVisible = false;
        VersionHint.Text = UiText.Dialog.LinuxFetchingReleases;
        try
        {
            var releases = await WineRunnersClient.FetchReleasesAsync();
            _releases = releases;
            PopulateVersionCombo();
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Wine.ReleasesFetchFailedFormat, ex.Message));
            VersionHint.Text = string.Empty;
            ErrorText.Text = string.Format(UiText.Dialog.LinuxReleasesFetchFailedFormat, ex.Message);
            ErrorText.IsVisible = true;
            RetryButton.IsVisible = true;
        }
    }

    private void PopulateVersionCombo()
    {
        var items = new List<string> { LatestKey };
        items.AddRange(_releases.Select(r => r.Version));
        VersionCombo.ItemsSource = items;

        var settings = SettingsService.Load();
        var idx = items.FindIndex(v => v.Equals(settings.WineVersion, StringComparison.OrdinalIgnoreCase));
        VersionCombo.SelectedIndex = idx >= 0 ? idx : 0;
        VersionHint.Text = UiText.Dialog.LinuxLatestHint;
        CheckForUpdate();
        RefreshSaveButtonLabel();
    }

    private async void OnRetry(object? sender, RoutedEventArgs e)
    {
        try
        {
            await LoadReleasesAsync();
        }
        catch
        {
            /* LoadReleasesAsync already surfaces errors via ErrorText */
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            await OnSaveAsync();
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Wine.LinuxSetupFailedFormat, ex.Message));
            ErrorText.Text = string.Format(UiText.Dialog.LinuxSetupFailedFormat, ex.Message);
            ErrorText.IsVisible = true;
            SaveButton.IsEnabled = true;
        }
    }

    private async Task OnSaveAsync()
    {
        var useBundled = BundledModeRadio.IsChecked == true;
        var installFonts = InstallFontsPill.IsChecked == true;
        var applyTheme = ApplyThemePill.IsChecked == true;
        var settings = SettingsService.Load();
        var wasBundled = settings.WineMode.Equals("Bundled", StringComparison.OrdinalIgnoreCase);
        var fontsWereInstalled = settings.WineInstallFonts;
        settings.WineMode = useBundled ? "Bundled" : "System";
        settings.WineInstallFonts = installFonts;
        settings.WineApplyTheme = applyTheme;

        if (!installFonts && fontsWereInstalled && Directory.Exists(WineService.FontsDir))
        {
            var confirm = new AppDialog(
                UiText.Dialog.LinuxDeleteFontsTitle,
                string.Format(UiText.Dialog.LinuxDeleteFontsBody, WineService.FontsDir),
                UiText.Button.WineDelete, UiText.Button.WineKeep);
            await confirm.ShowDialog(this);
            if (confirm.Result == UiText.Button.WineDelete)
                TryDeleteDir(WineService.FontsDir);
        }

        if (!useBundled)
        {
            if (wasBundled && Directory.Exists(WineService.PrefixesDir))
            {
                var confirm = new AppDialog(
                    UiText.Dialog.LinuxDeleteBundledTitle,
                    string.Format(UiText.Dialog.LinuxDeleteBundledBody, WineService.PrefixesDir),
                    UiText.Button.WineDelete, UiText.Button.WineKeep);
                await confirm.ShowDialog(this);
                if (confirm.Result == UiText.Button.WineDelete)
                {
                    TryDeleteDir(WineService.PrefixesDir);
                    TryDeleteDir(WineService.FallbackPrefixDir);
                }
            }
        }
        else
        {
            var pickedVersion = VersionCombo.SelectedItem as string ?? LatestKey;
            var release = WineRunnersClient.PickRelease(_releases, pickedVersion);
            if (release is null)
            {
                ErrorText.Text = UiText.Dialog.LinuxNoReleases;
                ErrorText.IsVisible = true;
                RetryButton.IsVisible = true;
                return;
            }

            if (!HasEnoughDiskSpace(out var freeGib))
            {
                ErrorText.Text = string.Format(UiText.Dialog.LinuxDiskSpaceInsufficientFormat, RequiredDiskGib, freeGib, WineService.LinuxDir);
                ErrorText.IsVisible = true;
                return;
            }

            if (!EnsurePrefixLocation())
                return;

            settings.WineVersion = pickedVersion;
            SettingsService.Save(settings);

            SaveButton.IsEnabled = false;
            RetryButton.IsVisible = false;
            ErrorText.IsVisible = false;
            ProgressPanel.IsVisible = true;
            ProgressText.Text = string.Format(UiText.Dialog.LinuxDownloadingArchiveFormat, release.ArchiveName);
            ProgressBar.Value = 0;

            var progress = new Progress<double>(p => ProgressBar.Value = p * 100);
            _downloadCts = new CancellationTokenSource();
            var runner = await WineRunnersClient.DownloadAndInstallAsync(release, progress, _downloadCts.Token);

            SaveButton.IsEnabled = true;
            if (runner is null)
            {
                ProgressPanel.IsVisible = false;
                ErrorText.Text = UiText.Dialog.LinuxDownloadFailed;
                ErrorText.IsVisible = true;
                RetryButton.IsVisible = true;
                return;
            }

            ProgressPanel.IsVisible = false;
        }

        if (installFonts && !FontsInstalled())
            await RunFontsDownloadAsync();

        settings.LinuxSetupCompleted = true;
        SettingsService.Save(settings);

        if (applyTheme)
        {
            var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            _ = WinePrefixTheme.ApplyAsync(isDark, true);
        }

        Close();
    }

    private async Task RunFontsDownloadAsync()
    {
        SaveButton.IsEnabled = false;
        RetryButton.IsVisible = false;
        ErrorText.IsVisible = false;
        ProgressPanel.IsVisible = true;
        ProgressText.Text = UiText.Dialog.LinuxDownloadingFonts;
        ProgressBar.Value = 0;
        var fontsProgress = new Progress<double>(p => ProgressBar.Value = p * 100);
        _downloadCts ??= new CancellationTokenSource();
        await WineFontsClient.EnsureDownloadedAsync(fontsProgress, _downloadCts.Token);
        SaveButton.IsEnabled = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        Close();
    }

    private bool EnsurePrefixLocation()
    {
        var defaultOk = WineService.SupportsWinePrefix(WineService.DefaultPrefixDir);
        double tmpFreeGib = 0;
        try
        {
            tmpFreeGib = new DriveInfo(Path.GetPathRoot("/tmp") ?? "/").AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        }
        catch
        {
            /* leave as 0 */
        }

        var tmpOk = WineService.SupportsWinePrefix("/tmp") && tmpFreeGib >= 5;

        var chosen = defaultOk ? WineService.DefaultPrefixDir
            : tmpOk ? WineService.FallbackPrefixDir
            : null;

        if (chosen == null)
        {
            ErrorText.Text = string.Format(UiText.Dialog.LinuxPrefixLocationFailedFormat, tmpFreeGib);
            ErrorText.IsVisible = true;
            return false;
        }

        try
        {
            Directory.CreateDirectory(chosen);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Wine.PrefixCreateFailedFormat, chosen, ex.Message));
            ErrorText.Text = string.Format(UiText.Dialog.LinuxPrefixCreateFailedFormat, chosen, ex.Message);
            ErrorText.IsVisible = true;
            return false;
        }

        return true;
    }

    private static bool HasEnoughDiskSpace(out double freeGib)
    {
        freeGib = 0;
        try
        {
            var dir = Directory.Exists(WineService.LinuxDir)
                ? WineService.LinuxDir
                : Path.GetDirectoryName(WineService.LinuxDir.TrimEnd(Path.DirectorySeparatorChar)) ?? AppContext.BaseDirectory;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)) ?? "/");
            freeGib = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            return drive.AvailableFreeSpace >= RequiredDiskGibBytes;
        }
        catch
        {
            return true;
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            /* best-effort; leftover files are harmless */
        }
    }
}