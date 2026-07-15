using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Apportia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Apportia.Views;

public partial class CustomAppWindow : Window
{
    private static readonly HashSet<string> RelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".bat", ".cmd", ".vbs", ".ico", ".png" };

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
    private int _galleryShift;
    private bool _galleryShifted;
    private bool _iconManuallySelected;
    private string _rawVersion = string.Empty;
    private bool _sectionManuallyEdited;
    private Border? _selectedThumb;
    private bool _suppressSectionEdit;
    private string? _tempIconPath;

    public CustomAppWindow()
    {
        InitializeComponent();
    }

    public CustomAppWindow(
        IReadOnlyList<string> categories,
        IReadOnlyDictionary<string, IReadOnlyList<string>> subCategoriesMap,
        string? presetFolder = null) : this()
    {
        _subCategoriesMap = subCategoriesMap;
        Title = UiText.Dialog.CustomAppImportTitle;
        ActionButton.Content = UiText.Button.CustomAppImport;
        CategoryCombo.ItemsSource = categories;
        if (categories.Count > 0)
            CategoryCombo.SelectedIndex = 0;

        if (string.IsNullOrEmpty(presetFolder))
            return;
        FolderBrowseButton.IsVisible = false;
        Dispatcher.UIThread.Post(() => _ = PopulateFromFolderAsync(presetFolder));
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

        Title = UiText.Dialog.CustomAppEditTitle;
        ActionButton.Content = UiText.Button.CustomAppSave;
        FolderSection.IsVisible = false;
        _suppressSectionEdit = true;
        SectionBox.Text = node.SectionName;
        _suppressSectionEdit = false;
        _sectionManuallyEdited = true;

        CategoryCombo.ItemsSource = categories;
        CategoryCombo.SelectedItem = node.Category;
        if (CategoryCombo.SelectedIndex < 0 && categories.Count > 0)
            CategoryCombo.SelectedIndex = 0;

        RefreshSubCategoryCombo(node.Category, node.SubCategory);

        NameBox.Text = node.Name;
        DescriptionBox.Text = node.Description;
        WebsiteBox.Text = node.Website;

        var (storedVersion, storedVersionSource, storedDisplayVersion) = CustomAppService.LoadVersionInfo(node.SectionName);

        _initializing = true;

        var scanned = ScanFiles(_appDir);

        var exeFiles = GetLaunchableFiles(_appDir, scanned);
        ExeSourceCombo.ItemsSource = exeFiles;
        ExeSourceCombo.SelectedItem = node.DownloadFile;
        if (ExeSourceCombo.SelectedIndex < 0 && exeFiles.Count > 0)
            ExeSourceCombo.SelectedIndex = 0;

        var versionItems = BuildVersionSourceItems(_appDir, scanned);
        VersionSourceCombo.ItemsSource = versionItems;
        var defaultVersionSource = string.IsNullOrEmpty(storedVersionSource)
            ? node.DownloadFile
            : storedVersionSource;
        var matchedVersionItem = versionItems.FirstOrDefault(s => s.Display.Equals(defaultVersionSource, StringComparison.OrdinalIgnoreCase));
        VersionSourceCombo.SelectedItem = matchedVersionItem ?? (versionItems.Count > 0 ? versionItems[0] : null);

        var sourceItems = BuildSourceItems(_appDir, scanned);
        IconSourceCombo.ItemsSource = sourceItems;
        var matchedItem = sourceItems.FirstOrDefault(s => s.Display.Equals(node.DownloadFile, StringComparison.OrdinalIgnoreCase));
        IconSourceCombo.SelectedItem = matchedItem ?? (sourceItems.Count > 0 ? sourceItems[0] : null);

        _initializing = false;

        _rawVersion = string.IsNullOrEmpty(storedDisplayVersion) ? storedVersion : storedDisplayVersion;
        VersionBox.Text = NormalizeVersion(storedVersion);

        if (VersionSourceCombo.SelectedItem is SourceItem versionItem && File.Exists(versionItem.FullPath))
        {
            var liveRaw = CustomAppService.ReadExeVersion(versionItem.FullPath);
            var liveNormalized = NormalizeVersion(liveRaw);
            if (!string.IsNullOrEmpty(liveNormalized) && liveNormalized != NormalizeVersion(storedVersion))
            {
                var previousText = string.IsNullOrEmpty(storedVersion) ? UiText.Dialog.CustomAppVersionUpdatedNone : NormalizeVersion(storedVersion);
                _rawVersion = liveRaw;
                VersionBox.Text = liveNormalized;
                VersionChangedText.Text = string.Format(UiText.Dialog.CustomAppVersionUpdatedFormat, previousText);
                VersionChangedText.IsVisible = true;
                ActionButton.Foreground = new SolidColorBrush(Color.Parse("#E0A020"));
            }
        }

        RefreshIconGallery();
    }

    public bool Success { get; private set; }
    public new string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Website { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string SubCategory { get; private set; } = string.Empty;
    public string DisplayVersion { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string JoinedDate { get; private set; } = string.Empty;
    public string UpdateDate { get; private set; } = string.Empty;
    public string ExeFile { get; private set; } = string.Empty;
    public string VersionSourceExe { get; private set; } = string.Empty;
    public string FolderName { get; private set; } = string.Empty;
    public string SectionName { get; private set; } = string.Empty;
    public string IconSourcePath { get; private set; } = string.Empty;

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiText.Dialog.CustomAppFolderPickerTitle,
                AllowMultiple = false
            });

            if (folders.Count == 0)
                return;
            var folder = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(folder))
                return;

            await PopulateFromFolderAsync(folder);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Custom.FolderPickerFailedFormat, ex.Message));
        }
    }

    private async Task PopulateFromFolderAsync(string folder)
    {
        try
        {
            FolderBox.Text = folder;
            _iconManuallySelected = false;

            var scanned = await Task.Run(() => ScanFiles(folder));
            var exeFiles = GetLaunchableFiles(folder, scanned);
            var versionItems = BuildVersionSourceItems(folder, scanned);
            var sourceItems = BuildSourceItems(folder, scanned);

            ExeSourceCombo.ItemsSource = exeFiles;
            ExeSourceCombo.SelectedIndex = exeFiles.Count > 0 ? 0 : -1;

            var selectedExe = ExeSourceCombo.SelectedItem as string;
            if (!_sectionManuallyEdited && !string.IsNullOrEmpty(selectedExe))
                SetSectionBoxFromExe(selectedExe);

            VersionSourceCombo.ItemsSource = versionItems;
            var matchedVersionItem = versionItems.FirstOrDefault(s => s.Display.Equals(selectedExe, StringComparison.OrdinalIgnoreCase));
            VersionSourceCombo.SelectedItem = matchedVersionItem ?? (versionItems.Count > 0 ? versionItems[0] : null);

            IconSourceCombo.ItemsSource = sourceItems;
            var matchedItem = sourceItems.FirstOrDefault(s => s.Display.Equals(selectedExe, StringComparison.OrdinalIgnoreCase));
            IconSourceCombo.SelectedItem = matchedItem ?? (sourceItems.Count > 0 ? sourceItems[0] : null);

            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));

            var appInfoPath = Path.Combine(folder, "App", "AppInfo", "appinfo.ini");
            if (!File.Exists(appInfoPath))
                return;
            var details = AppInfoReader.Read(appInfoPath);
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
            {
                _rawVersion = details.PackageVersion;
                VersionBox.Text = NormalizeVersion(details.PackageVersion);
            }
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Custom.PopulateFromFolderFailedFormat, ex.Message));
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
        if (ExeSourceCombo.SelectedItem is not string exeFile)
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
                if (!File.Exists(exePath))
                    return;
                var (name, desc) = PeReader.ReadVersionInfo(exePath);
                NameBox.Text = name;
                DescriptionBox.Text = desc;
            }

            return;
        }

        if (!_sectionManuallyEdited)
            SetSectionBoxFromExe(exeFile);

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

    private void SetSectionBoxFromExe(string exeFile)
    {
        var baseName = Path.GetFileNameWithoutExtension(exeFile);
        if (string.IsNullOrEmpty(baseName))
            return;
        _suppressSectionEdit = true;
        SectionBox.Text = baseName;
        _suppressSectionEdit = false;
    }

    private void OnSectionBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_initializing || _suppressSectionEdit)
            return;
        _sectionManuallyEdited = true;
    }

    private void OnVersionExeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        if (VersionSourceCombo.SelectedItem is not SourceItem item || !File.Exists(item.FullPath))
            return;
        var raw = CustomAppService.ReadExeVersion(item.FullPath);
        _rawVersion = raw;
        VersionBox.Text = NormalizeVersion(raw);
    }

    private void OnIconExeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _iconManuallySelected)
            return;
        if (IconSourceCombo.SelectedItem is not SourceItem)
            return;
        RefreshIconGallery();
    }

    private void RefreshSubCategoryCombo(string category, string selectedSubCategory)
    {
        var prefix = category + " \u2013 ";
        var subs = _subCategoriesMap.TryGetValue(category, out var list) ? list : [];
        var items = new List<string> { string.Empty };
        items.AddRange(subs.Where(s => !s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
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
                    icons.Add(new IconVariant(bmp, string.Format(UiText.Dialog.CustomAppIconCurrentFormat, px.Width, px.Height)));
                }
                catch
                {
                    /* bitmap may be corrupt – skip it */
                }
            }
        }

        if (IconSourceCombo.SelectedItem is SourceItem item && File.Exists(item.FullPath))
            icons.AddRange(LoadIcons(item.FullPath));

        PopulateIconGallery(icons);
    }

    private static Dictionary<string, List<string>> ScanFiles(string rootDir)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(rootDir))
            return result;
        foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!RelevantExtensions.Contains(ext))
                continue;
            if (!result.TryGetValue(ext, out var list))
                result[ext] = list = [];
            list.Add(file);
        }

        return result;
    }

    private static List<string> GetLaunchableFiles(string rootDir, Dictionary<string, List<string>> files)
    {
        return new[] { ".exe", ".bat", ".cmd", ".vbs" }
               .SelectMany(ext => files.TryGetValue(ext, out var l) ? l : [])
               .Select(f => Path.GetRelativePath(rootDir, f))
               .OrderBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
               .ThenBy(f => Path.GetExtension(f).ToLowerInvariant() switch
               {
                   ".exe" => 0,
                   ".bat" => 1,
                   ".cmd" => 2,
                   _ => 3
               })
               .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
               .ToList();
    }

    private static List<SourceItem> BuildVersionSourceItems(string rootDir, Dictionary<string, List<string>> files)
    {
        return new[] { ".exe", ".dll" }
               .SelectMany(ext => files.TryGetValue(ext, out var l) ? l : [])
               .Select(f => new SourceItem(Path.GetRelativePath(rootDir, f), f))
               .OrderBy(s => Path.GetDirectoryName(s.Display) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
               .ThenBy(s => Path.GetExtension(s.FullPath).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
               .ThenBy(s => Path.GetFileName(s.Display), StringComparer.OrdinalIgnoreCase)
               .ToList();
    }

    private static List<SourceItem> BuildSourceItems(string rootDir, Dictionary<string, List<string>> files)
    {
        return new[] { ".exe", ".ico", ".png" }
               .SelectMany(ext => files.TryGetValue(ext, out var l) ? l : [])
               .Select(f => new SourceItem(Path.GetRelativePath(rootDir, f), f))
               .OrderBy(s => Path.GetDirectoryName(s.Display) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
               .ThenBy(s => Path.GetExtension(s.FullPath).ToLowerInvariant() switch
               {
                   ".exe" => 0,
                   ".ico" => 1,
                   _ => 2
               })
               .ThenBy(s => Path.GetFileName(s.Display), StringComparer.OrdinalIgnoreCase)
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
            return [new IconVariant(bmp, string.Format(UiText.Dialog.CustomAppIconSizeFormat, Path.GetFileName(fullPath), px.Width, px.Height))];
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
        _selectedThumb?.Classes.Remove("selected");

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
                Title = UiText.Dialog.CustomAppIconPickerTitle,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(UiText.Dialog.CustomAppIconFileTypeName) { Patterns = ["*.png"] }
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
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Custom.IconPickerFailedFormat, ex.Message));
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
            ShowError(UiText.Dialog.CustomAppSelectFolder);
            return;
        }

        if (ExeSourceCombo.SelectedItem is not string exeFile || string.IsNullOrEmpty(exeFile))
        {
            ShowError(UiText.Dialog.CustomAppSelectExe);
            return;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError(UiText.Dialog.CustomAppEnterName);
            return;
        }

        var sectionInput = SectionBox.Text?.Trim() ?? string.Empty;
        var sectionError = CustomAppService.ValidateSectionName(sectionInput);
        if (sectionError != null)
        {
            ShowError(sectionError);
            return;
        }

        var effectiveIconPath = string.IsNullOrEmpty(IconBox.Text) ? _tempIconPath : IconBox.Text;
        if (string.IsNullOrWhiteSpace(effectiveIconPath))
        {
            ShowError(UiText.Dialog.CustomAppSelectIcon);
            return;
        }

        SectionName = sectionInput;
        FolderName = FolderBox.Text.Trim();
        ExeFile = exeFile;
        Category = CategoryCombo.SelectedItem as string ?? string.Empty;
        SubCategory = SubCategoryCombo.SelectedItem as string ?? string.Empty;
        Name = NameBox.Text.Trim();
        Description = DescriptionBox.Text?.Trim() ?? string.Empty;
        Website = WebsiteBox.Text?.Trim() ?? string.Empty;
        Version = VersionBox.Text?.Trim() ?? string.Empty;
        VersionSourceExe = (VersionSourceCombo.SelectedItem as SourceItem)?.Display ?? string.Empty;
        var versionRelPath = string.IsNullOrEmpty(VersionSourceExe) ? exeFile : VersionSourceExe;
        var versionPath = Path.Combine(FolderName, versionRelPath);
        UpdateDate = File.Exists(versionPath)
            ? File.GetLastWriteTime(versionPath).ToString("yyyy-MM-dd")
            : string.Empty;
        DisplayVersion = string.IsNullOrEmpty(_rawVersion) ? Version : _rawVersion;
        JoinedDate = DateTime.Today.ToString("yyyy-MM-dd");
        IconSourcePath = effectiveIconPath;
        Success = true;
        Close();
    }

    private void DoSave()
    {
        ErrorText.IsVisible = false;

        if (ExeSourceCombo.SelectedItem is not string exeFile || string.IsNullOrEmpty(exeFile))
        {
            ShowError(UiText.Dialog.CustomAppSelectExe);
            return;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError(UiText.Dialog.CustomAppEnterName);
            return;
        }

        var sectionInput = SectionBox.Text?.Trim() ?? string.Empty;
        var sectionError = CustomAppService.ValidateSectionName(sectionInput, _sectionName);
        if (sectionError != null)
        {
            ShowError(sectionError);
            return;
        }

        var sectionChanged = !string.Equals(sectionInput, _sectionName, StringComparison.Ordinal);
        if (sectionChanged && RunningAppsService.IsRunning(_sectionName))
        {
            ShowError(UiText.Dialog.CustomAppSectionRunning);
            return;
        }

        var effectiveIconPath = string.IsNullOrEmpty(IconBox.Text) ? _tempIconPath : IconBox.Text;

        SectionName = sectionInput;
        ExeFile = exeFile;
        Category = CategoryCombo.SelectedItem as string ?? string.Empty;
        SubCategory = SubCategoryCombo.SelectedItem as string ?? string.Empty;
        Name = NameBox.Text.Trim();
        Description = DescriptionBox.Text?.Trim() ?? string.Empty;
        Website = WebsiteBox.Text?.Trim() ?? string.Empty;
        Version = VersionBox.Text?.Trim() ?? string.Empty;
        VersionSourceExe = (VersionSourceCombo.SelectedItem as SourceItem)?.Display ?? string.Empty;
        var versionRelPath = string.IsNullOrEmpty(VersionSourceExe) ? exeFile : VersionSourceExe;
        var versionPath = Path.Combine(_appDir, versionRelPath);
        UpdateDate = File.Exists(versionPath)
            ? File.GetLastWriteTime(versionPath).ToString("yyyy-MM-dd")
            : string.Empty;
        DisplayVersion = string.IsNullOrEmpty(_rawVersion) ? Version : _rawVersion;
        IconSourcePath = effectiveIconPath ?? string.Empty;
        Success = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var parts = raw.Trim().Split('.');
        var result = new List<string>(4);
        foreach (var part in parts)
        {
            if (result.Count == 4)
                break;
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0)
                break;
            result.Add(digits);
        }

        if (result.Count == 0)
            return string.Empty;

        while (result.Count < 4)
            result.Add("0");

        return string.Join('.', result);
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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
        var screen = Screens.ScreenFromWindow(this);
        if (screen is not null && screen.WorkingArea.Height > 0)
        {
            var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
            MaxHeight = screen.WorkingArea.Height / scale * 0.85;
        }

        if (IconGalleryBorder.IsVisible)
            ShiftForGallery(true);
        IconGalleryBorder.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name == "IsVisible")
                ShiftForGallery((bool)args.NewValue!);
        };
    }

    private void ShiftForGallery(bool show)
    {
        if (!show)
        {
            if (!_galleryShifted)
                return;
            _galleryShifted = false;
            Position = new PixelPoint(Position.X, Position.Y + _galleryShift);
            return;
        }

        if (_galleryShifted)
            return;

        if (_galleryShift > 0)
        {
            _galleryShifted = true;
            Position = new PixelPoint(Position.X, Position.Y - _galleryShift);
            return;
        }

        var heightBefore = ClientSize.Height;
        Dispatcher.UIThread.Post(() =>
        {
            var half = (int)((ClientSize.Height - heightBefore) / 2);
            if (half <= 0)
                return;
            _galleryShift = half;
            _galleryShifted = true;
            Position = new PixelPoint(Position.X, Position.Y - half);
        }, DispatcherPriority.Background);
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
