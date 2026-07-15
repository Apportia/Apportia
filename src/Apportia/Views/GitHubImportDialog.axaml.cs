using System.IO.Compression;
using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class GitHubImportDialog : Window
{
    private readonly Func<string, string, string, string, string, Task<bool>>? _confirmHashMismatch;
    private List<GhAsset> _assets = [];
    private double _lastHeight;
    private GhRelease? _release;

    public GitHubImportDialog()
    {
        InitializeComponent();
        PropertyChanged += OnPropertyChanged;
    }

    public GitHubImportDialog(Func<string, string, string, string, string, Task<bool>> confirmHashMismatch) : this()
    {
        _confirmHashMismatch = confirmHashMismatch;
    }

    public bool Success { get; private set; }
    public string ExtractedFolder { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string DisplayVersion { get; private set; } = string.Empty;
    public string UpdateDate { get; private set; } = string.Empty;
    public string UpdateUrl { get; private set; } = string.Empty;
    public string UpdateFile { get; private set; } = string.Empty;
    public DateTime? UpdateFileMtime { get; private set; }

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
    }

    private async void OnFetch(object? sender, RoutedEventArgs e)
    {
        try
        {
            var owner = OwnerBox.Text?.Trim() ?? string.Empty;
            var repo = RepoBox.Text?.Trim() ?? string.Empty;
            if (owner.Length == 0)
            {
                ShowError(UiText.Dialog.GitHubImportEnterOwner);
                return;
            }

            if (repo.Length == 0)
            {
                ShowError(UiText.Dialog.GitHubImportEnterRepo);
                return;
            }

            HideError();
            ReleasePanel.IsVisible = false;
            DownloadButton.IsVisible = false;
            ShowStatus(UiText.Dialog.GitHubImportFetching);
            FetchButton.IsEnabled = false;

            var release = await GitHubClient.FetchLatestReleaseAsync($"{owner}/{repo}");

            HideStatus();
            FetchButton.IsEnabled = true;

            if (release is null)
            {
                ShowError(UiText.Dialog.GitHubImportFetchFailed);
                return;
            }

            if (release.TagName.Length == 0)
            {
                ShowError(UiText.Dialog.GitHubImportNoRelease);
                return;
            }

            _release = release;
            _assets = release.Assets.ToList();

            TagBox.Text = release.TagName;
            NameBox.Text = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name;
            PublishedBox.Text = release.PublishedAt is { } dt
                ? RelativeDate.Format(dt.LocalDateTime)
                : string.Empty;

            var sevenZipAvailable = AppDeployService.FindSevenZip(AppDeployService.AppsDir) != null;
            _assets = _assets.Where(a => IsSupportedAsset(a.Name, sevenZipAvailable)).ToList();

            AssetCombo.ItemsSource = _assets.Select(a => a.Name).ToList();
            var firstZip = _assets.FindIndex(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            AssetCombo.SelectedIndex = firstZip >= 0 ? firstZip : _assets.Count > 0 ? 0 : -1;

            ReleasePanel.IsVisible = true;
            DownloadButton.IsVisible = _assets.Count > 0;
            if (_assets.Count == 0)
                ShowError(UiText.Dialog.GitHubImportNoSupportedAssets);
        }
        catch (Exception ex)
        {
            FetchButton.IsEnabled = true;
            HideStatus();
            ShowError(ex.Message);
        }
    }

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_release is null || AssetCombo.SelectedIndex < 0 || AssetCombo.SelectedIndex >= _assets.Count)
                return;

            var asset = _assets[AssetCombo.SelectedIndex];
            var isSevenZip = asset.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
            var isZip = asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (!isZip && !isSevenZip)
            {
                ShowError(UiText.Dialog.GitHubImportUnsupportedAsset);
                return;
            }

            var sevenZipPath = isSevenZip ? AppDeployService.FindSevenZip(AppDeployService.AppsDir) : null;
            if (isSevenZip && sevenZipPath == null)
            {
                ShowError(UiText.Dialog.GitHubImportSevenZipMissing);
                return;
            }

            var repo = RepoBox.Text?.Trim() ?? string.Empty;
            var extension = isSevenZip ? ".7z" : ".zip";
            var tempZip = Path.Combine(Path.GetTempPath(), $"apportia_gh_{Guid.NewGuid():N}{extension}");

            try
            {
                HideError();
                DownloadButton.IsEnabled = false;
                FetchButton.IsEnabled = false;
                ShowStatus(string.Format(UiText.Dialog.GitHubImportDownloadingFormat, asset.Name));

                var ok = await GitHubClient.DownloadAssetAsync(asset.DownloadUrl, tempZip);
                if (!ok)
                {
                    ShowError(UiText.Dialog.GitHubImportDownloadFailed);
                    DownloadButton.IsEnabled = true;
                    FetchButton.IsEnabled = true;
                    HideStatus();
                    return;
                }

                var sha256 = asset.Sha256Hex;
                if (sha256.Length > 0 && AppDeployService.VerifyHash(tempZip, sha256) == HashResult.Invalid)
                {
                    var proceed = _confirmHashMismatch != null &&
                                  await _confirmHashMismatch(repo, repo, asset.Name, sha256, tempZip);
                    if (!proceed)
                    {
                        ShowError(UiText.Dialog.InstallHashMismatchBody);
                        DownloadButton.IsEnabled = true;
                        FetchButton.IsEnabled = true;
                        HideStatus();
                        return;
                    }
                }

                ShowStatus(UiText.Dialog.GitHubImportExtracting);

                var folderName = CustomAppService.ReserveUniqueFolderName(repo);
                var destDir = Path.Combine(CustomAppService.CustomAppsDir, folderName);
                try
                {
                    if (isSevenZip)
                        await AppDeployService.ExtractAsync(sevenZipPath!, tempZip, destDir);
                    else
                        await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, destDir));
                }
                catch (Exception ex)
                {
                    if (Directory.Exists(destDir))
                    {
                        try
                        {
                            Directory.Delete(destDir, true);
                        }
                        catch
                        {
                            // best-effort cleanup
                        }
                    }

                    ShowError($"{UiText.Dialog.GitHubImportExtractionFailed}\n\n{ex.Message}");
                    DownloadButton.IsEnabled = true;
                    FetchButton.IsEnabled = true;
                    HideStatus();
                    return;
                }

                ExtractedFolder = destDir;
                Version = GitHubVersion.Derive(_release.TagName, _release.PublishedAt);
                DisplayVersion = _release.TagName;
                var publishedLocal = _release.PublishedAt?.LocalDateTime ?? DateTime.Today;
                UpdateDate = publishedLocal.ToString("yyyy-MM-dd");
                UpdateUrl = $"https://github.com/{OwnerBox.Text?.Trim()}/{repo}";
                UpdateFile = asset.Name;
                UpdateFileMtime = publishedLocal;
                Success = true;
                Close();
            }
            finally
            {
                try
                {
                    if (File.Exists(tempZip))
                        File.Delete(tempZip);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            DownloadButton.IsEnabled = true;
            FetchButton.IsEnabled = true;
            HideStatus();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError()
    {
        ErrorText.IsVisible = false;
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    private void HideStatus()
    {
        StatusText.IsVisible = false;
    }

    private static bool IsSupportedAsset(string name, bool sevenZipAvailable)
    {
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return true;
        return sevenZipAvailable && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
    }
}