using Apportia.Platform;
using Apportia.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class LinuxSetupDialog : Window
{
    private const string LatestKey = "latest";
    private const long RequiredDiskGib = 2;
    private const long RequiredDiskGibBytes = RequiredDiskGib * 1024L * 1024L * 1024L;

    private CancellationTokenSource? _downloadCts;
    private double _lastHeight;
    private IReadOnlyList<WineRunnerRelease> _releases = [];

    public LinuxSetupDialog()
    {
        InitializeComponent();
        PropertyChanged += OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != HeightProperty)
            return;
        var newHeight = Height;
        if (_lastHeight <= 0 || double.IsNaN(newHeight))
        {
            _lastHeight = newHeight;
            return;
        }

        var delta = newHeight - _lastHeight;
        _lastHeight = newHeight;
        var shift = (int)(delta / 2);
        if (shift == 0)
            return;
        Position = new PixelPoint(Position.X, Position.Y - shift);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);

        var settings = SettingsService.Load();
        var systemAvailable = WineService.IsSystemWineAvailable();

        SystemWinePanel.IsVisible = systemAvailable;
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

        UpdateVersionPanelVisibility();
        _ = LoadReleasesAsync();
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        UpdateVersionPanelVisibility();
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
        VersionHint.Text = "Fetching Wine releases...";
        try
        {
            var releases = await WineRunnersClient.FetchReleasesAsync();
            _releases = releases;
            PopulateVersionCombo();
        }
        catch (Exception ex)
        {
            VersionHint.Text = string.Empty;
            ErrorText.Text = $"Could not fetch Wine releases: {ex.Message}";
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
        VersionHint.Text = "\"latest\" automatically updates to the newest staging build.";
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
            ErrorText.Text = $"Setup failed: {ex.Message}";
            ErrorText.IsVisible = true;
            SaveButton.IsEnabled = true;
        }
    }

    private async Task OnSaveAsync()
    {
        var useBundled = BundledModeRadio.IsChecked == true;
        var settings = SettingsService.Load();
        var wasBundled = settings.WineMode.Equals("Bundled", StringComparison.OrdinalIgnoreCase);
        settings.WineMode = useBundled ? "Bundled" : "System";

        if (!useBundled)
        {
            if (wasBundled && Directory.Exists(WineService.LinuxDir))
            {
                var confirm = new AppDialog(
                    "Delete bundled Wine files?",
                    $"Switching to system Wine.\n\n" +
                    $"The bundled Wine installation and prefix in\n\n" +
                    $"{WineService.LinuxDir}\n\n" +
                    "are no longer needed. Delete them now?",
                    "Delete", "Keep");
                await confirm.ShowDialog(this);
                if (confirm.Result == "Delete")
                    TryDeleteDir(WineService.LinuxDir);
            }

            settings.LinuxSetupCompleted = true;
            SettingsService.Save(settings);
            Close();
            return;
        }

        var pickedVersion = VersionCombo.SelectedItem as string ?? LatestKey;
        var release = WineRunnersClient.PickRelease(_releases, pickedVersion);
        if (release is null)
        {
            ErrorText.Text = "No Wine release available. Check your connection and retry.";
            ErrorText.IsVisible = true;
            RetryButton.IsVisible = true;
            return;
        }

        if (!HasEnoughDiskSpace(out var freeGib))
        {
            ErrorText.Text = $"Not enough free disk space for the bundled Wine prefix. " +
                             $"At least {RequiredDiskGib} GiB required, only {freeGib:0.0} GiB available in {WineService.LinuxDir}.";
            ErrorText.IsVisible = true;
            return;
        }

        settings.WineVersion = pickedVersion;
        SettingsService.Save(settings);

        SaveButton.IsEnabled = false;
        RetryButton.IsVisible = false;
        ErrorText.IsVisible = false;
        ProgressPanel.IsVisible = true;
        ProgressText.Text = $"Downloading {release.ArchiveName}...";
        ProgressBar.Value = 0;

        var progress = new Progress<double>(p => ProgressBar.Value = p * 100);
        _downloadCts = new CancellationTokenSource();
        var runner = await WineRunnersClient.DownloadAndInstallAsync(release, progress, _downloadCts.Token);

        SaveButton.IsEnabled = true;
        if (runner is null)
        {
            ProgressPanel.IsVisible = false;
            ErrorText.Text = "Download or extraction failed.";
            ErrorText.IsVisible = true;
            RetryButton.IsVisible = true;
            return;
        }

        Directory.CreateDirectory(WineService.PrefixDir);
        settings.LinuxSetupCompleted = true;
        SettingsService.Save(settings);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        Close();
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