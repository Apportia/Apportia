using Apportia.Services;
using Apportia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Apportia.Views;

public partial class CustomAppWindow : Window
{
    private readonly string _appDir = string.Empty;
    private readonly string _initialDescription = string.Empty;
    private readonly string _initialExe = string.Empty;
    private readonly bool _initializing;
    private readonly string _initialName = string.Empty;
    private readonly bool _isEditMode;
    private readonly string _sectionName = string.Empty;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _subCategoriesMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    private List<IconVariant> _galleryIcons = [];
    private bool _iconManuallySelected;
    private Border? _selectedThumb;
    private string? _tempIconPath;

    public CustomAppWindow()
    {
        InitializeComponent();
    }

    public CustomAppWindow(
        IReadOnlyList<string> categories,
        IReadOnlyDictionary<string, IReadOnlyList<string>> subCategoriesMap) : this()
    {
        _subCategoriesMap = subCategoriesMap;
        Title = "Import App";
        ActionButton.Content = "Import";
        CategoryCombo.ItemsSource = categories;
        if (categories.Count > 0)
            CategoryCombo.SelectedIndex = 0;
    }

    public CustomAppWindow(
        AppNode node,
        IReadOnlyList<string> categories,
        IReadOnlyDictionary<string, IReadOnlyList<string>> subCategoriesMap) : this()
    {
        _isEditMode = true;
        _subCategoriesMap = subCategoriesMap;
        _appDir = Path.Combine(CustomAppService.CustomAppsDir, node.SectionName);
        _sectionName = node.SectionName;
        _initialExe = node.DownloadFile;
        _initialName = node.Name;
        _initialDescription = node.Description;

        Title = "App Settings";
        ActionButton.Content = "Save";
        FolderSection.IsVisible = false;

        CategoryCombo.ItemsSource = categories;
        CategoryCombo.SelectedItem = node.Category;
        if (CategoryCombo.SelectedIndex < 0 && categories.Count > 0)
            CategoryCombo.SelectedIndex = 0;

        RefreshSubCategoryCombo(node.Category, node.SubCategory);

        NameBox.Text = node.Name;
        DescriptionBox.Text = node.Description;
        WebsiteBox.Text = node.AppUrl;

        var (storedVersion, storedVersionSource) = CustomAppService.LoadVersionInfo(node.SectionName);

        _initializing = true;

        var exeFiles = Directory.Exists(_appDir)
            ? Directory.GetFiles(_appDir, "*.exe", SearchOption.TopDirectoryOnly)
                       .Select(Path.GetFileName)
                       .Where(f => !string.IsNullOrEmpty(f))
                       .Order()
                       .ToList()
            : [];
        ExeCombo.ItemsSource = exeFiles;
        ExeCombo.SelectedItem = node.DownloadFile;
        if (ExeCombo.SelectedIndex < 0 && exeFiles.Count > 0)
            ExeCombo.SelectedIndex = 0;

        var versionItems = BuildVersionSourceItems(_appDir);
        VersionExeCombo.ItemsSource = versionItems;
        var defaultVersionSource = string.IsNullOrEmpty(storedVersionSource)
            ? node.DownloadFile
            : storedVersionSource;
        var matchedVersionItem = versionItems.FirstOrDefault(s => s.Display.Equals(defaultVersionSource, StringComparison.OrdinalIgnoreCase));
        VersionExeCombo.SelectedItem = matchedVersionItem ?? (versionItems.Count > 0 ? versionItems[0] : null);

        var sourceItems = BuildSourceItems(_appDir);
        IconExeCombo.ItemsSource = sourceItems;
        var matchedItem = sourceItems.FirstOrDefault(s => s.Display.Equals(node.DownloadFile, StringComparison.OrdinalIgnoreCase));
        IconExeCombo.SelectedItem = matchedItem ?? (sourceItems.Count > 0 ? sourceItems[0] : null);

        _initializing = false;

        VersionBox.Text = storedVersion;
        RefreshIconGallery();
    }

    public bool Success { get; private set; }
    public string SelectedFolder { get; private set; } = string.Empty;
    public string SelectedExe { get; private set; } = string.Empty;
    public string AppVersion { get; private set; } = string.Empty;
    public string AppUpdateDate { get; private set; } = string.Empty;
    public string VersionSourceExe { get; private set; } = string.Empty;
    public string SelectedCategory { get; private set; } = string.Empty;
    public string SelectedSubCategory { get; private set; } = string.Empty;
    public string AppName { get; private set; } = string.Empty;
    public string AppDescription { get; private set; } = string.Empty;
    public string AppWebsite { get; private set; } = string.Empty;
    public string IconSourcePath { get; private set; } = string.Empty;

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select App Folder",
                AllowMultiple = false
            });

            if (folders.Count == 0)
                return;
            var folder = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(folder))
                return;

            FolderBox.Text = folder;
            _iconManuallySelected = false;

            var exeFiles = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                                    .Select(Path.GetFileName)
                                    .Where(f => !string.IsNullOrEmpty(f))
                                    .Order()
                                    .ToList();

            ExeCombo.ItemsSource = exeFiles;
            ExeCombo.SelectedIndex = exeFiles.Count > 0 ? 0 : -1;

            var selectedExe = ExeCombo.SelectedItem as string;

            var versionItems = BuildVersionSourceItems(folder);
            VersionExeCombo.ItemsSource = versionItems;
            var matchedVersionItem = versionItems.FirstOrDefault(s => s.Display.Equals(selectedExe, StringComparison.OrdinalIgnoreCase));
            VersionExeCombo.SelectedItem = matchedVersionItem ?? (versionItems.Count > 0 ? versionItems[0] : null);

            var sourceItems = BuildSourceItems(folder);
            IconExeCombo.ItemsSource = sourceItems;
            var matchedItem = sourceItems.FirstOrDefault(s => s.Display.Equals(selectedExe, StringComparison.OrdinalIgnoreCase));
            IconExeCombo.SelectedItem = matchedItem ?? (sourceItems.Count > 0 ? sourceItems[0] : null);

            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));

            var appInfoPath = Path.Combine(folder, "App", "AppInfo", "appinfo.ini");
            if (!File.Exists(appInfoPath))
                return;
            var details = IniParser.ReadAppInfoDetails(appInfoPath);
            if (!string.IsNullOrEmpty(details.Name))
                NameBox.Text = details.Name;
            if (!string.IsNullOrEmpty(details.Description))
                DescriptionBox.Text = details.Description;
            if (!string.IsNullOrEmpty(details.Category))
                CategoryCombo.SelectedItem = details.Category;
            if (!string.IsNullOrEmpty(details.SubCategory))
                SubCategoryCombo.SelectedItem = details.SubCategory;
            if (!string.IsNullOrEmpty(details.Homepage))
                WebsiteBox.Text = details.Homepage;
            if (!string.IsNullOrEmpty(details.PackageVersion))
                VersionBox.Text = details.PackageVersion;
        }
        catch
        {
            /* folder picker or file access failed – leave form unchanged */
        }
    }

    private void OnCategorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        RefreshSubCategoryCombo(CategoryCombo.SelectedItem as string ?? string.Empty, string.Empty);
    }

    private void OnExeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        if (ExeCombo.SelectedItem is not string exeFile)
            return;

        if (_isEditMode)
        {
            if (exeFile == _initialExe)
            {
                NameBox.Text = _initialName;
                DescriptionBox.Text = _initialDescription;
            }
            else
            {
                var exePath = Path.Combine(_appDir, exeFile);
                if (File.Exists(exePath))
                {
                    var (name, desc) = PeReader.ReadVersionInfo(exePath);
                    NameBox.Text = name;
                    DescriptionBox.Text = desc;
                }
            }

            return;
        }

        var folder = FolderBox.Text;
        if (string.IsNullOrEmpty(folder))
            return;
        var path = Path.Combine(folder, exeFile);
        if (!File.Exists(path))
            return;
        var (n, d) = PeReader.ReadVersionInfo(path);
        NameBox.Text = n;
        DescriptionBox.Text = d;
    }

    private void OnVersionExeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        if (VersionExeCombo.SelectedItem is not SourceItem item || !File.Exists(item.FullPath))
            return;
        VersionBox.Text = CustomAppService.ReadExeVersion(item.FullPath);
    }

    private void OnIconExeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _iconManuallySelected)
            return;
        if (IconExeCombo.SelectedItem is not SourceItem)
            return;
        RefreshIconGallery();
    }

    private void RefreshSubCategoryCombo(string category, string selectedSubCategory)
    {
        var subs = _subCategoriesMap.TryGetValue(category, out var list) ? list : [];
        var items = new List<string> { string.Empty };
        items.AddRange(subs);
        SubCategoryCombo.ItemsSource = items;
        SubCategoryCombo.SelectedItem = selectedSubCategory;
        if (SubCategoryCombo.SelectedIndex < 0)
            SubCategoryCombo.SelectedIndex = 0;
    }

    private void RefreshIconGallery()
    {
        var icons = new List<IconVariant>();

        if (_isEditMode)
        {
            var currentIconPath = Path.Combine(CustomAppService.CustomAppImagesDir, _sectionName + ".png");
            if (File.Exists(currentIconPath))
            {
                try
                {
                    var bmp = new Bitmap(currentIconPath);
                    var px = bmp.PixelSize;
                    icons.Add(new IconVariant(bmp, $"Current icon ({px.Width} x {px.Height} px)"));
                }
                catch
                {
                    /* bitmap may be corrupt – skip it */
                }
            }
        }

        if (IconExeCombo.SelectedItem is SourceItem item && File.Exists(item.FullPath))
            icons.AddRange(LoadIcons(item.FullPath));

        PopulateIconGallery(icons);
    }

    private static List<SourceItem> BuildVersionSourceItems(string rootDir)
    {
        if (!Directory.Exists(rootDir))
            return [];

        return Directory.GetFiles(rootDir, "*.exe", SearchOption.AllDirectories)
                        .Select(f => new SourceItem(Path.GetRelativePath(rootDir, f), f))
                        .OrderBy(s => s.Display)
                        .ToList();
    }

    private static List<SourceItem> BuildSourceItems(string rootDir)
    {
        if (!Directory.Exists(rootDir))
            return [];

        return Directory.GetFiles(rootDir, "*.exe", SearchOption.AllDirectories)
                        .Concat(Directory.GetFiles(rootDir, "*.ico", SearchOption.AllDirectories))
                        .Concat(Directory.GetFiles(rootDir, "*.png", SearchOption.AllDirectories))
                        .Select(f => new SourceItem(Path.GetRelativePath(rootDir, f), f))
                        .OrderBy(s => s.Display)
                        .ToList();
    }

    private static List<IconVariant> LoadIcons(string fullPath)
    {
        if (fullPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            return PeReader.ReadIcoFile(fullPath);

        if (!fullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return PeReader.TryExtractAllIcons(fullPath);

        try
        {
            var bmp = new Bitmap(fullPath);
            var px = bmp.PixelSize;
            return [new IconVariant(bmp, $"{Path.GetFileName(fullPath)} ({px.Width} x {px.Height} px)")];
        }
        catch
        {
            /* corrupt or unreadable image file – skip it */
            return [];
        }
    }

    private void PopulateIconGallery(List<IconVariant> icons)
    {
        DisposeGalleryIcons();
        IconGallery.Children.Clear();
        _selectedThumb = null;

        _galleryIcons = icons;
        IconGalleryBorder.IsVisible = icons.Count > 0;

        for (var i = 0; i < icons.Count; i++)
        {
            var idx = i;
            var border = new Border
            {
                Classes = { "iconThumb" },
                Child = BuildThumbImage(icons[i].Icon)
            };
            ToolTip.SetTip(border, icons[i].Tooltip);
            border.Tapped += (_, _) => SelectGalleryThumb(idx, border);
            IconGallery.Children.Add(border);
        }

        if (icons.Count > 0)
            SelectGalleryThumb(0, (Border)IconGallery.Children[0]);
    }

    private static Image BuildThumbImage(Bitmap icon)
    {
        const double maxDisplay = 44.0;
        var natural = Math.Max(icon.PixelSize.Width, icon.PixelSize.Height);
        var size = Math.Min(natural, maxDisplay);
        var image = new Image { Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform };
        if (natural > maxDisplay)
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        return image;
    }

    private void OnIconScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        IconScrollViewer.Offset = new Vector(
            IconScrollViewer.Offset.X - e.Delta.Y * 50,
            IconScrollViewer.Offset.Y);
        e.Handled = true;
    }

    private void SelectGalleryThumb(int index, Border thumb)
    {
        if (_selectedThumb is not null)
            _selectedThumb.Classes.Remove("selected");

        _selectedThumb = thumb;
        thumb.Classes.Add("selected");

        CleanupTempIcon();
        _tempIconPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        try
        {
            _galleryIcons[index].Icon.Save(_tempIconPath);
            IconBox.Text = string.Empty;
        }
        catch
        {
            /* icon save failed – clean up the incomplete temp file so IconBox stays empty */
            CleanupTempIcon();
        }
    }

    private async void OnBrowseIcon(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Icon (PNG)",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }
                ]
            });

            if (files.Count == 0)
                return;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                return;

            if (_selectedThumb is not null)
            {
                _selectedThumb.Classes.Remove("selected");
                _selectedThumb = null;
            }

            CleanupTempIcon();
            _iconManuallySelected = true;
            IconBox.Text = path;
        }
        catch
        {
            /* file picker or icon load failed – leave form unchanged */
        }
    }

    private void OnAction(object? sender, RoutedEventArgs e)
    {
        if (_isEditMode)
            DoSave();
        else
            DoImport();
    }

    private void DoImport()
    {
        ErrorText.IsVisible = false;

        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            ShowError("Please select an app folder.");
            return;
        }

        if (ExeCombo.SelectedItem is not string exeFile || string.IsNullOrEmpty(exeFile))
        {
            ShowError("Please select an executable.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError("Please enter a name.");
            return;
        }

        var effectiveIconPath = string.IsNullOrEmpty(IconBox.Text) ? _tempIconPath : IconBox.Text;
        if (string.IsNullOrWhiteSpace(effectiveIconPath))
        {
            ShowError("Please select an icon.");
            return;
        }

        SelectedFolder = FolderBox.Text.Trim();
        SelectedExe = exeFile;
        SelectedCategory = CategoryCombo.SelectedItem as string ?? string.Empty;
        SelectedSubCategory = SubCategoryCombo.SelectedItem as string ?? string.Empty;
        AppName = NameBox.Text.Trim();
        AppDescription = DescriptionBox.Text?.Trim() ?? string.Empty;
        AppWebsite = WebsiteBox.Text?.Trim() ?? string.Empty;
        AppVersion = VersionBox.Text?.Trim() ?? string.Empty;
        VersionSourceExe = (VersionExeCombo.SelectedItem as SourceItem)?.Display ?? string.Empty;
        var versionRelPath = string.IsNullOrEmpty(VersionSourceExe) ? exeFile : VersionSourceExe;
        var versionPath = Path.Combine(SelectedFolder, versionRelPath);
        AppUpdateDate = File.Exists(versionPath)
            ? File.GetLastWriteTime(versionPath).ToString("yyyy-MM-dd")
            : string.Empty;
        IconSourcePath = effectiveIconPath;
        Success = true;
        Close();
    }

    private void DoSave()
    {
        ErrorText.IsVisible = false;

        if (ExeCombo.SelectedItem is not string exeFile || string.IsNullOrEmpty(exeFile))
        {
            ShowError("Please select an executable.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError("Please enter a name.");
            return;
        }

        var effectiveIconPath = string.IsNullOrEmpty(IconBox.Text) ? _tempIconPath : IconBox.Text;

        SelectedExe = exeFile;
        SelectedCategory = CategoryCombo.SelectedItem as string ?? string.Empty;
        SelectedSubCategory = SubCategoryCombo.SelectedItem as string ?? string.Empty;
        AppName = NameBox.Text.Trim();
        AppDescription = DescriptionBox.Text?.Trim() ?? string.Empty;
        AppWebsite = WebsiteBox.Text?.Trim() ?? string.Empty;
        AppVersion = VersionBox.Text?.Trim() ?? string.Empty;
        VersionSourceExe = (VersionExeCombo.SelectedItem as SourceItem)?.Display ?? string.Empty;
        var versionRelPath = string.IsNullOrEmpty(VersionSourceExe) ? exeFile : VersionSourceExe;
        var versionPath = Path.Combine(_appDir, versionRelPath);
        AppUpdateDate = File.Exists(versionPath)
            ? File.GetLastWriteTime(versionPath).ToString("yyyy-MM-dd")
            : string.Empty;
        IconSourcePath = effectiveIconPath ?? string.Empty;
        Success = true;
        Close();
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

    private void CleanupTempIcon()
    {
        if (_tempIconPath is null)
            return;

        try
        {
            File.Delete(_tempIconPath);
        }
        catch
        {
            /* file may already be gone */
        }

        _tempIconPath = null;
    }

    private void DisposeGalleryIcons()
    {
        foreach (var v in _galleryIcons)
            v.Icon.Dispose();
        _galleryIcons = [];
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!Success)
            CleanupTempIcon();
        DisposeGalleryIcons();
    }

    private sealed record SourceItem(string Display, string FullPath)
    {
        public override string ToString()
        {
            return Display;
        }
    }
}
