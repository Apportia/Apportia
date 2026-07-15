using System.Diagnostics;
using Apportia.Models;
using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Apportia.ViewModels;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Apportia.Views;

public partial class MainWindow : Window, IInstallUi
{
    private readonly CancellationTokenSource _cts = new();
    private readonly AppDeployService _deployService;
    private readonly AppImageManager _iconManager;
    private readonly InstallOrchestrator _installer;
    private readonly InstallQueue _installQueue = new();
    private readonly List<string> _ipcArgBatch = [];
    private readonly ThemeController _themeController;

    private bool _activateOnSearchClose;

    private string[] _cliAppArgs;

    private bool _ctrlHeld;
    private FilterViewSettings _defaultView = new();

    private bool _forceClose;
    private ScrollViewer? _gridScrollerCache;
    private int _historyIndex = -1;
    private CancellationTokenSource? _iconDownloadCts;
    private CancellationTokenSource? _ipcDebounceCts;
    private IpcServer? _ipcServer;

    private ScrollViewer? _listScrollerCache;
    private string? _pendingScrollApp;
    private string? _pendingScrollTarget;
    private bool _pendingScrollTop;
    private double? _pendingScrollY;
    private List<string> _searchHistory = [];

    private SelfUpdateCoordinator _selfUpdate = null!;

    private bool _suppressWindowTransition;

    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Avoids a flash at XAML defaults before StartupAsync runs.
        ApplyPersistedShell();

        var cliArgs = Environment.GetCommandLineArgs();
        _cliAppArgs = cliArgs.Length > 1 ? Environment.GetCommandLineArgs().Skip(1).ToArray() : [];

        Log.Clear();

        var iconCacheDir = Path.Combine(AppContext.BaseDirectory, "Data", "AppImages");

        _iconManager = new AppImageManager(iconCacheDir);
        _deployService = new AppDeployService(AppDeployService.AppsDir);
        _installer = new InstallOrchestrator(_installQueue, _deployService, this);
        _themeController = new ThemeController(this, ThemeToggleIcon);

        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        _ = Task.Run(() => AtomicFile.SweepStaleTempFiles(dataDir, TimeSpan.FromMinutes(5)));

        BuildSkeletons();
        PreloadToolbarLabels();

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (File.Exists(AppDatabaseUpdater.CachePath))
                await PopulateFromCache();
            await StartupAsync();
            await WaitForRowsFullyLoadedAsync();
            Dispatcher.UIThread.Post(HideLoadingOverlay, DispatcherPriority.Loaded);
        }, DispatcherPriority.Background);
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static string OpenFolderIconPath => IsWindows
        ? "avares://Apportia/Assets/Emoji/win/1f4c1.svg"
        : "avares://Apportia/Assets/Emoji/1f4c1.svg";

    private ItemsControl ActiveList =>
        (DataContext as MainViewModel)?.Columns.IsGridView == true ? MainGridList : MainList;

    private ScrollViewer? ListScroller =>
        _listScrollerCache ??= MainList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private ScrollViewer? GridScroller =>
        _gridScrollerCache ??= MainGridList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private ScrollViewer? ActiveScroller =>
        (DataContext as MainViewModel)?.Columns.IsGridView == true ? GridScroller : ListScroller;

    Task<string?> IInstallUi.ShowDialogAsync(AppNode? node, string title, string message, params string[] buttons)
    {
        return node != null ? ShowDialog(node, title, message, buttons) : ShowDialog(title, message, buttons);
    }

    Task<string?> IInstallUi.ShowDiskSpaceDialogAsync(AppNode node, string appName, long required, long available)
    {
        return ShowDiskSpaceDialog(node, appName, required, available);
    }

    async Task<string?> IInstallUi.ShowLanguageDialogAsync(AppNode node, IReadOnlyList<string> keys, string? savedLang)
    {
        var dialog = new LanguageDialog(node.Name, keys, savedLang) { Icon = new WindowIcon(node.Icon) };
        await dialog.ShowDialog(this);
        return dialog.SelectedLanguageKey;
    }

    async Task<int[]?> IInstallUi.ShowJavaRequiredDialogAsync(AppNode node, string[] pluginNames)
    {
        var dialog = new JavaRequiredDialog(node.Name, pluginNames) { Icon = new WindowIcon(node.Icon) };
        await dialog.ShowDialog(this);
        return dialog.SelectedIndices;
    }

    async Task<string?> IInstallUi.ShowMirrorDialogAsync(AppNode node, string? failedSlug, IReadOnlyList<(string Slug, string Label)> available)
    {
        var dialog = new MirrorDialog(node.Name, failedSlug, available) { Icon = new WindowIcon(node.Icon) };
        await dialog.ShowDialog(this);
        return dialog.SelectedMirror;
    }

    Task IInstallUi.ShowVirusTotalDialogAsync(AppNode node)
    {
        return new VirusTotalDialog(node) { Icon = new WindowIcon(node.Icon) }.ShowDialog(this);
    }

    void IInstallUi.ShowDownloadBar(bool visible)
    {
        ShowDownloadBar(visible);
    }

    void IInstallUi.SetDownloadStatus(string sizeText, string speedText)
    {
        DownloadSizeText.Text = sizeText;
        DownloadSpeedText.Text = speedText;
    }

    void IInstallUi.SetDownloadProgress(double percent, bool indeterminate)
    {
        DownloadProgressBar.IsIndeterminate = indeterminate;
        DownloadProgressBar.Value = percent;
    }

    void IInstallUi.SetInstalling(bool value)
    {
        SetInstalling(value);
    }

    void IInstallUi.SetBusyCursor(bool busy)
    {
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
    }

    Task<string?> IInstallUi.ResolveAppExeAsync(AppNode node, string appsBaseDir)
    {
        return ResolveAppExeAsync(node, appsBaseDir);
    }

    Task IInstallUi.LaunchAsync(AppNode node)
    {
        return TryLaunchWithArgsAsync(node);
    }

    IEnumerable<AppNode> IInstallUi.GetAllNodes()
    {
        return DataContext is MainViewModel vm ? vm.AllNodes : [];
    }

    Task<bool> IInstallUi.EnsureWineReadyAsync()
    {
        return EnsureWineReadyAsync();
    }

    private double? GetViewportY(Control container)
    {
        var target = (Visual?)ActiveScroller;
        return target == null ? null : container.TranslatePoint(new Point(0, 0), target)?.Y;
    }

    private void SetActiveScrollY(double y)
    {
        var scroller = ActiveScroller;
        scroller?.Offset = new Vector(0, Math.Max(0, y));
    }

    private double GetActiveScrollY()
    {
        return ActiveScroller?.Offset.Y ?? 0;
    }

    private void ScrollContainerToViewportY(Control container, double targetViewportY)
    {
        var scroller = ActiveScroller;
        if (scroller == null) return;
        var y = GetViewportY(container);
        if (y == null) return;
        scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y + (y.Value - targetViewportY)));
    }

    private void PreloadToolbarLabels()
    {
        var settings = SettingsService.Load();
        var initialFilter = CurrentAppService.LoadAll().Count > 0
            ? InstallFilter.Installed
            : InstallFilter.All;
        var preset = settings.ViewPresets.GetValueOrDefault(initialFilter.ToString())
                     ?? FilterViewSettings.Default;

        ApplyToolbarLabelsFromPreset(preset, initialFilter);
    }

    private void ApplyToolbarLabelsFromPreset(FilterViewSettings preset, InstallFilter? filter = null)
    {
        IconSizeButton.Content = string.Format(UiText.Status.IconSizeFormat, preset.IconSize);
        ViewModeButton.Content = preset.IsGridView ? UiText.Status.ViewModeGrid : UiText.Status.ViewModeList;
        FontSizeButton.Content = string.Format(UiText.Status.FontSizeFormat, preset.FontSize);
        CategoryScopeButton.Content = preset.CategoryScope switch
        {
            CategoryScope.Full => UiText.Status.CategoryScopeFull,
            CategoryScope.Extended => UiText.Status.CategoryScopeExtended,
            _ => UiText.Status.CategoryScopeStandard
        };
        CategoryDisplayButton.Content = preset.CategoryDisplay switch
        {
            CategoryDisplayMode.Categories => UiText.Status.CategoryDisplayCategories,
            CategoryDisplayMode.None => UiText.Status.CategoryDisplayNoGroups,
            _ => UiText.Status.CategoryDisplayTree
        };
        if (filter != null)
            InstallFilterButton.Content = filter switch
            {
                InstallFilter.Installed => UiText.Status.InstallFilterInstalled,
                InstallFilter.NotInstalled => UiText.Status.InstallFilterNotInstalled,
                _ => UiText.Status.InstallFilterAll
            };
    }

    private void BuildSkeletons(FilterViewSettings? presetOverride = null)
    {
        FilterViewSettings preset;
        if (presetOverride != null)
        {
            preset = presetOverride;
        }
        else
        {
            var settings = SettingsService.Load();
            preset = settings.ViewPresets.GetValueOrDefault(
                         (CurrentAppService.LoadAll().Count > 0
                             ? InstallFilter.Installed
                             : InstallFilter.All).ToString())
                     ?? FilterViewSettings.Default;
        }

        var isGrid = preset.IsGridView;
        var isCategorized = isGrid && preset.CategoryDisplay != CategoryDisplayMode.None;
        ListSkeleton.IsVisible = !isGrid;
        GridSkeleton.IsVisible = isGrid && !isCategorized;
        CategoryGridSkeleton.IsVisible = isCategorized;

        var viewportW = presetOverride != null
            ? preset.WindowWidth
            : ResolveViewport(Bounds.Width, Width, MinWidth);
        var viewportH = presetOverride != null
            ? preset.WindowHeight
            : ResolveViewport(Bounds.Height, Height, MinHeight);

        var chromeH = TopToolbar.Bounds.Height
                      + (ColumnHeaderBar.IsVisible ? ColumnHeaderBar.Bounds.Height : 0)
                      + (DownloadBar.IsVisible ? DownloadBar.Bounds.Height : 0);
        if (chromeH > 0)
            viewportH = Math.Max(100, viewportH - chromeH);

        var minSkeletonW = presetOverride != null
            ? Math.Max(0, preset.WindowWidth - ListSkeleton.Margin.Left - ListSkeleton.Margin.Right)
            : 0d;
        ListSkeleton.MinWidth = minSkeletonW;
        GridSkeleton.MinWidth = minSkeletonW;

        if (isCategorized)
        {
            CategoryGridSkeleton.Children.Clear();
            var tileSide = Math.Max(preset.IconSize + 40, 140);
            var cols = Math.Max(1, (int)(viewportW / tileSide));
            const double headerHeight = 22;
            const double sectionSpacing = 10;
            var tileCounts = new[] { 2, 3, 4, 2, 5, 3 };
            var headerWidths = new[] { 1.0, 0.55, 0.8, 0.4, 0.7, 0.5 };
            var used = 0d;
            var sectionIndex = 0;
            while (used < viewportH + tileSide)
            {
                var tiles = Math.Min(cols, tileCounts[sectionIndex % tileCounts.Length]);
                var headerFrac = headerWidths[sectionIndex % headerWidths.Length];
                CategoryGridSkeleton.Children.Add(BuildCategoryHeaderBar(headerFrac, headerHeight));
                CategoryGridSkeleton.Children.Add(BuildCategoryTileRow(tiles, cols, preset.IconSize));
                used += headerHeight + tileSide + sectionSpacing;
                sectionIndex++;
            }
        }
        else if (isGrid)
        {
            GridSkeleton.Children.Clear();
            var tileSide = Math.Max(preset.IconSize + 40, 140);
            var cols = Math.Max(1, (int)(viewportW / tileSide));
            var rowsCount = (int)Math.Ceiling(viewportH / tileSide) + 2;
            GridSkeleton.Columns = cols;
            var count = cols * rowsCount;
            for (var i = 0; i < count; i++)
                GridSkeleton.Children.Add(BuildGridSkeletonCard(preset.IconSize));
        }
        else
        {
            ListSkeleton.Children.Clear();
            var rowHeight = Math.Max(46, preset.IconSize + 14);
            var count = (int)Math.Ceiling(viewportH / rowHeight) + 4;
            for (var i = 0; i < count; i++)
                ListSkeleton.Children.Add(BuildListSkeletonRow(preset.IconSize, i));
        }

        return;

        static double ResolveViewport(double bounds, double requested, double min)
        {
            return new[] { bounds, requested, min }.FirstOrDefault(v => !double.IsNaN(v) && v > 0);
        }
    }

    private static Border BuildGridSkeletonCard(int iconSize)
    {
        var iconSide = Math.Max(12, iconSize);
        var iconPlaceholder = WrapSkeleton(new Border
        {
            Width = iconSide,
            Height = iconSide,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var titleBar = WrapSkeleton(new Border
        {
            Height = 10,
            Margin = new Thickness(8, 8, 8, 4),
            CornerRadius = new CornerRadius(3)
        });
        var subBar = WrapSkeleton(new Border
        {
            Height = 8,
            Margin = new Thickness(16, 0, 16, 8),
            CornerRadius = new CornerRadius(3)
        });
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(iconPlaceholder);
        stack.Children.Add(titleBar);
        stack.Children.Add(subBar);
        return new Border
        {
            Padding = new Thickness(8, 12),
            Child = stack
        };
    }

    private static Control BuildCategoryHeaderBar(double widthFraction, double height)
    {
        var bar = WrapSkeleton(new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 2, 0, 2)
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(widthFraction, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0.0001, 1 - widthFraction), GridUnitType.Star)));
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);
        return grid;
    }

    private static Control BuildCategoryTileRow(int tileCount, int totalCols, int iconSize)
    {
        var grid = new UniformGrid { Columns = totalCols, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var i = 0; i < tileCount; i++)
            grid.Children.Add(BuildGridSkeletonCard(iconSize));
        return grid;
    }

    private static Border BuildListSkeletonRow(int iconSize, int index)
    {
        var iconSide = Math.Max(12, iconSize);
        var iconPlaceholder = WrapSkeleton(new Border
        {
            Width = iconSide,
            Height = iconSide,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        });
        var titleWidths = new[] { 0.35, 0.42, 0.28, 0.5, 0.38 };
        var titleBar = WrapSkeleton(new Border
        {
            Height = 10,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var subBar = WrapSkeleton(new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        var textStack = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        textStack.Children.Add(BuildProportionalBar(titleBar, titleWidths[index % titleWidths.Length]));
        textStack.Children.Add(subBar);
        var panel = new DockPanel { LastChildFill = true, Height = Math.Max(42, iconSide + 10) };
        DockPanel.SetDock(iconPlaceholder, Dock.Left);
        panel.Children.Add(iconPlaceholder);
        panel.Children.Add(textStack);
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = panel
        };
    }

    private static Panel WrapSkeleton(Border baseBorder)
    {
        baseBorder.Classes.Add("skeletonBase");
        var overlay = new Border { CornerRadius = baseBorder.CornerRadius };
        overlay.Classes.Add("skeletonShimmer");
        var container = new Grid
        {
            ClipToBounds = true,
            Margin = baseBorder.Margin,
            HorizontalAlignment = baseBorder.HorizontalAlignment,
            VerticalAlignment = baseBorder.VerticalAlignment,
            Width = baseBorder.Width,
            Height = baseBorder.Height
        };
        baseBorder.Margin = default;
        baseBorder.Width = double.NaN;
        baseBorder.Height = double.NaN;
        baseBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
        baseBorder.VerticalAlignment = VerticalAlignment.Stretch;
        container.Children.Add(baseBorder);
        container.Children.Add(overlay);
        AttachShimmerAnimation(overlay, container, baseBorder.CornerRadius);
        return container;
    }

    private static void AttachShimmerAnimation(Border overlay, Control container, CornerRadius cornerRadius)
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            var w = container.Bounds.Width;
            var h = container.Bounds.Height;
            if (w <= 0 || h <= 0)
                return;
            var visual = ElementComposition.GetElementVisual(overlay);
            if (visual == null)
                return;
            container.LayoutUpdated -= handler;
            var radius = cornerRadius.TopLeft;
            if (radius > 0)
                container.Clip = new RectangleGeometry(new Rect(0, 0, w, h))
                {
                    RadiusX = radius,
                    RadiusY = radius
                };
            var anim = visual.Compositor.CreateVector3DKeyFrameAnimation();
            anim.InsertKeyFrame(0f, new Vector3D(-w, 0, 0));
            anim.InsertKeyFrame(1f, new Vector3D(w, 0, 0));
            anim.Duration = TimeSpan.FromMilliseconds(1500);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Offset", anim);
        };
        container.LayoutUpdated += handler;
    }

    private static Grid BuildProportionalBar(Control bar, double fraction)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(fraction, GridUnitType.Star),
                new ColumnDefinition(1 - fraction, GridUnitType.Star)
            },
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);
        return grid;
    }

    private Task WaitForRowsFullyLoadedAsync()
    {
        if (DataContext is not MainViewModel { IsBuildingRows: true } vm)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RowsFullyLoaded += Handler;
        return tcs.Task;

        void Handler()
        {
            vm.RowsFullyLoaded -= Handler;
            tcs.TrySetResult();
        }
    }

    private void ShowLoadingOverlay(FilterViewSettings? preset = null)
    {
        BuildSkeletons(preset);
        if (preset != null)
            ColumnHeaderBar.IsVisible = !preset.IsGridView;
        LoadingOverlay.Opacity = 1;
        LoadingOverlay.IsVisible = true;
        TopToolbar.IsEnabled = false;
    }

    private async Task SwitchFilterAsync(MainViewModel vm, InstallFilter target)
    {
        if (vm.InstallFilter == target)
            return;

        var targetPreset = SettingsService.Load().ViewPresets.GetValueOrDefault(target.ToString())
                           ?? _defaultView;
        ApplyToolbarLabelsFromPreset(targetPreset, target);
        ShowLoadingOverlay(targetPreset);
        await Task.Yield();

        ListsHost.IsVisible = false;
        try
        {
            await StartWindowTransitionToPreset(targetPreset, true);
        }
        finally
        {
            ListsHost.IsVisible = true;
        }

        _suppressWindowTransition = true;
        try
        {
            vm.BeginPresetUpdate();
            try
            {
                vm.InstallFilter = target;
            }
            finally
            {
                vm.EndPresetUpdate();
            }
        }
        finally
        {
            _suppressWindowTransition = false;
        }

        await Task.WhenAny(WaitForRowsFullyLoadedAsync(), Task.Delay(250));
        HideLoadingOverlay();
    }

    private void HideLoadingOverlay()
    {
        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(250),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(OpacityProperty, 1d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(OpacityProperty, 0d) }
                }
            }
        };
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await anim.RunAsync(LoadingOverlay);
            LoadingOverlay.IsVisible = false;
            TopToolbar.IsEnabled = true;
        });
    }

    private async Task StartupAsync()
    {
        if (DataContext is MainViewModel)
        {
            if (await ResolveUnknownAppDirsAsync())
                await PopulateFromCache();
            _ = Task.WhenAll(
                AppDatabaseUpdater.TryUpdateAsync(_cts.Token),
                MirrorService.TryUpdateAsync(_cts.Token),
                SecurityNoticeService.TryUpdateAsync(_cts.Token));
            await VerifyInstalledAppsAsync();
            return;
        }

        await StartFirstRunAsync();
    }

    private async Task VerifyInstalledAppsAsync()
    {
        var result = await Task.Run(CurrentAppService.VerifyAgainstDisk);

        if (result.StructureChanged)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var moved = await ResolveUnknownAppDirsAsync();
                if (moved || result.StructureChanged)
                    await PopulateFromCache();
            });
        }

        if (result.CurrentDates.Count > 0)
            await Dispatcher.UIThread.InvokeAsync(() => ApplyCurrentDates(result.CurrentDates));
    }

    private void ApplyCurrentDates(IReadOnlyDictionary<string, string> dates)
    {
        if (DataContext is not MainViewModel vm)
            return;
        foreach (var node in vm.AllNodes)
            if (dates.TryGetValue(node.SectionName, out var d) && node.CurrentDate != d)
                node.CurrentDate = d;
    }

    private Task PopulateFromCache()
    {
        var vm = BuildViewModel();
        SubscribeViewModel(vm);
        DataContext = vm;
        UpdateTerminateAllButton();
        ApplyViewPreset(vm, false);
        _ = CheckCustomAppUpdatesAsync(vm);
        // In All/NotInstalled the merge flickers, so only stream upstream in for Installed.
        return vm.InstallFilter == InstallFilter.Installed
            ? MergeUpstreamAsync(vm)
            : Task.CompletedTask;
    }

    private async Task CheckCustomAppUpdatesAsync(MainViewModel vm)
    {
        try
        {
            await CustomAppUpdateChecker.CheckAsync(vm.AllNodes.Where(n => n.IsCustom), _cts.Token);
        }
        catch
        {
            // best-effort: skip when the atom feed is unreachable
        }

        RefreshUpdateButton();
    }

    private async Task MergeUpstreamAsync(MainViewModel vm)
    {
        var upstream = await Task.Run(() =>
                                          AppDatabaseParser.ParseJson(AppDatabaseUpdater.CachePath)
                                                           .ToList());
        vm.MergeUpstreamEntries(upstream);
        _ = StartIconDownloadsAsync(vm);
    }

    private MainViewModel BuildViewModel()
    {
        var settings = SettingsService.Load();
        _defaultView = FilterViewSettings.Default;

        var initialFilter = CurrentAppService.LoadAll().Count > 0
            ? InstallFilter.Installed
            : InstallFilter.All;

        var entries = initialFilter == InstallFilter.Installed
            ? BuildInstalledEntries()
            : AppDatabaseParser.ParseJson(AppDatabaseUpdater.CachePath);

        var vm = new MainViewModel(entries, _iconManager, _defaultView.IconSize)
        {
            Columns =
            {
                Name = settings.ColumnName,
                Version = settings.ColumnVersion,
                Download = settings.ColumnDownload,
                Install = settings.ColumnInstall,
                Joined = settings.ColumnJoined,
                Updated = settings.ColumnUpdated,
                Used = settings.ColumnUsed
            }
        };

        vm.Columns.SetSort(settings.SortColumn, settings.SortDescending);
        vm.Columns.IconSize = _defaultView.IconSize;
        vm.Columns.FontSize = _defaultView.FontSize;
        vm.CategoryDisplay = _defaultView.CategoryDisplay;
        vm.CategoryScope = _defaultView.CategoryScope;
        vm.Columns.IsGridView = _defaultView.IsGridView;
        vm.InstallFilter = initialFilter;

        return vm;
    }

    private static string BuildInstallPrompt(AppNode node, string appsBaseDir)
    {
        var prompt = string.Format(UiText.Dialog.MainInstallPromptFormat, node.Name);
        var required = (node.DownloadSizeMb + node.InstallSizeMb) * 1_048_576L;
        if (required <= 0)
            return prompt;
        var free = AppDiskUsageService.GetAvailableFreeSpace(appsBaseDir);
        return prompt + string.Format(
            UiText.Dialog.MainInstallPromptSizeSuffixFormat,
            AppDiskUsageService.FormatSize(required, floor: true),
            AppDiskUsageService.FormatSize(free, floor: true));
    }

    private static IReadOnlyList<AppEntry> BuildInstalledEntries()
    {
        var db = CurrentAppService.LoadAll();
        var result = new List<AppEntry>(db.Count);
        foreach (var (section, info) in db)
        {
            result.Add(new AppEntry(
                           section,
                           info.Name,
                           info.Description,
                           info.Website,
                           string.IsNullOrEmpty(info.Category) ? UiText.Dialog.MainCategoryAdvanced : info.Category,
                           info.SubCategory,
                           info.JoinedDate,
                           info.DisplayVersion,
                           info.PackageVersion,
                           info.UpdateDate,
                           info.DownloadFile,
                           string.Empty,
                           string.Empty,
                           string.Empty,
                           string.Empty,
                           string.Empty));
        }

        return result;
    }

    private void ApplyPersistedShell()
    {
        var settings = SettingsService.Load();
        var shellFilter = CurrentAppService.LoadAll().Count > 0
            ? InstallFilter.Installed
            : InstallFilter.All;
        settings.ViewPresets.TryGetValue(shellFilter.ToString(), out var preset);
        preset ??= FilterViewSettings.Default;
        Width = preset.WindowWidth;
        Height = preset.WindowHeight;

        if (Application.Current is { } app)
            app.RequestedThemeVariant = settings.Theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => null
            };
    }

    private async Task StartFirstRunAsync()
    {
        await Task.WhenAll(
            AppDatabaseUpdater.TryUpdateAsync(_cts.Token),
            MirrorService.TryUpdateAsync(_cts.Token),
            SecurityNoticeService.TryUpdateAsync(_cts.Token));
        if (!File.Exists(AppDatabaseUpdater.CachePath))
            return;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await PopulateFromCache();
            if (await ResolveUnknownAppDirsAsync())
                await PopulateFromCache();
        });
        _ = VerifyInstalledAppsAsync();
    }

    private async Task<bool> ResolveUnknownAppDirsAsync()
    {
        if (DataContext is not MainViewModel vm)
            return false;
        var dirs = CurrentAppService.ConsumePendingUnknownDirs();
        if (dirs.Count == 0)
            return false;

        var changed = false;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;
            var name = Path.GetFileName(dir);
            var choose = new AppDialog(
                UiText.Dialog.MainUnknownAppFolderTitle,
                string.Format(UiText.Dialog.MainUnknownAppFolderBodyFormat, name),
                UiText.Button.MoveToCustomApps, UiText.Button.Delete, UiText.Button.Skip);
            await choose.ShowDialog(this);

            switch (choose.Result)
            {
                case UiText.Button.MoveToCustomApps:
                {
                    if (await ImportUnknownAsCustomAsync(dir, vm))
                        changed = true;
                    break;
                }
                case UiText.Button.Delete:
                {
                    var confirm = new AppDialog(
                        UiText.Dialog.MainDeleteFolderTitle,
                        string.Format(UiText.Dialog.MainDeleteFolderBodyFormat, name),
                        UiText.Button.Delete, UiText.Button.Cancel);
                    await confirm.ShowDialog(this);
                    if (confirm.Result == UiText.Button.Delete)
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                        }
                        catch
                        {
                            /* best effort – dir may be in use */
                        }
                    }

                    break;
                }
            }
        }

        return changed;
    }

    private async Task<bool> ImportUnknownAsCustomAsync(string dir, MainViewModel vm)
    {
        var win = new CustomAppWindow(vm.Categories, vm.SubCategoriesMap, dir);
        await win.ShowDialog(this);
        if (!win.Success)
            return false;

        try
        {
            await CustomAppService.ImportAppAsync(
                win.FolderName,
                win.ExeFile,
                win.Name,
                win.Description,
                win.Website,
                win.IconSourcePath,
                win.Category,
                win.SubCategory,
                win.Version,
                win.VersionSourceExe,
                win.DisplayVersion,
                mode: ImportMode.Move,
                preferredFolderName: win.SectionName);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.ImportUnknownAsCustomFailedFormat, ex.Message));
            return false;
        }

        var iconPath = win.IconSourcePath;
        if (!iconPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            File.Delete(iconPath);
        }
        catch
        {
            /* file may already be gone */
        }

        return true;
    }

    private async Task StartIconDownloadsAsync(MainViewModel vm)
    {
        if (_iconDownloadCts is not null)
            await _iconDownloadCts.CancelAsync();
        _iconDownloadCts?.Dispose();
        _iconDownloadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = _iconDownloadCts.Token;

        var expectedSize = vm.Columns.IconLoadSize;
        var hideGames = vm.CategoryScope == CategoryScope.Standard;

        var nodes = vm.AllNodes.Where(n =>
        {
            if (n.IsCustom)
                return false;

            switch (vm.InstallFilter)
            {
                case InstallFilter.Installed when !n.IsInstalled:
                case InstallFilter.NotInstalled when n.IsInstalled:
                    return false;
            }

            return n.IsInstalled switch
            {
                false when n.IsAdvanced && vm.CategoryScope == CategoryScope.Standard => false,
                false when n.IsLegacy && vm.CategoryScope != CategoryScope.Full => false,
                false when string.Equals(n.Category, UiText.Dialog.MainCategoryGames, StringComparison.OrdinalIgnoreCase) && hideGames => false,
                _ => true
            };
        }).ToList();

        var nodeLookup = nodes.ToLookup(n => n.SectionName, StringComparer.OrdinalIgnoreCase);

        await _iconManager.DownloadAllAsync(
            nodeLookup.Select(g => g.Key),
            expectedSize,
            (section, bitmap) =>
            {
                if (vm.Columns.IconLoadSize != expectedSize)
                    return;
                foreach (var node in nodeLookup[section])
                    Dispatcher.UIThread.Post(() => node.Icon = bitmap);
            },
            ct);
    }

    private void OnCategoryHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: CategoryNode node })
            node.IsExpanded = !node.IsExpanded;
    }

    private void OnSubCategoryHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: SubCategoryNode node })
            node.IsExpanded = !node.IsExpanded;
    }

    private void OnStickyHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var name = StickyHeaderText.Text;
        for (var i = 0; i < vm.FlatRows.Count; i++)
        {
            if (vm.FlatRows[i] is not CategoryNode node || node.Category != name)
                continue;
            ScrollToFlatRowIndex(i, GetStickyOffset(vm));
            return;
        }
    }

    private void ScrollToFlatRowIndex(int index, double viewportTargetY)
    {
        if (DataContext is not MainViewModel vm) return;
        var node = index >= 0 && index < vm.FlatRows.Count ? vm.FlatRows[index] : null;
        if (node != null) ScrollToNode(node, viewportTargetY);
    }

    private void ScrollToNode(object node, double viewportTargetY)
    {
        if (DataContext is not MainViewModel vm) return;
        var list = ActiveList;
        var scroller = ActiveScroller;
        if (scroller == null) return;

        var idx = vm.Columns.IsGridView ? FindGridRowIndexFor(node, vm) : vm.FlatRows.IndexOf(node);
        if (idx < 0) return;

        var c = list.ContainerFromIndex(idx);
        if (c != null)
        {
            ScrollContainerToViewportY(c, viewportTargetY);
            return;
        }

        if (list is not ListBox lb)
            return;
        lb.ScrollIntoView(idx);
        Dispatcher.UIThread.Post(() =>
        {
            var c2 = list.ContainerFromIndex(idx);
            if (c2 != null) ScrollContainerToViewportY(c2, viewportTargetY);
        }, DispatcherPriority.Background);
    }

    private static int FindGridRowIndexFor(object node, MainViewModel vm)
    {
        for (var i = 0; i < vm.GridRows.Count; i++)
        {
            var r = vm.GridRows[i];
            if (ReferenceEquals(r, node) || node is AppNode app && r is GridTileRow row && row.Tiles.Contains(app))
                return i;
        }

        return -1;
    }

    private void OnMainGridListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel { Columns.IsGridView: true } vm)
            RecomputeGridColumns(vm);
    }

    private static void ReclaimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    }

    private void RecomputeGridColumns(MainViewModel vm)
    {
        var tileWidth = vm.Columns.TileWidth;
        if (tileWidth <= 0)
            return;
        var w = MainGridList.Bounds.Width;
        if (w <= 0)
            return;
        var cols = Math.Max(1, (int)Math.Floor(w / tileWidth));
        vm.GridColumns = cols;
    }

    private void OnAppRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ctrlHeld = (e.KeyModifiers & KeyModifiers.Control) != 0;
    }

    private async void OnAppRowTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (sender is not Border { DataContext: AppNode node })
                return;
            await ActivateNode(node, _ctrlHeld);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.RowTappedActivationFailedFormat, ex.Message));
        }
    }

    private async Task ActivateNode(AppNode node, bool ctrlHeld = false)
    {
        if (node.IsCustom)
        {
            if (!node.NeedsUpdate)
            {
                await TryLaunchWithArgsAsync(node);
                return;
            }

            if (ctrlHeld)
            {
                await UpdateCustomAppAsync(node, false);
                return;
            }

            var choice = await ShowDialog(
                node, string.Format(UiText.Dialog.MainUpdateAvailableFormat, node.Name),
                string.Format(UiText.Dialog.MainUpdateAvailableBodyFormat, node.Name),
                UiText.Button.UpdateAndRun, UiText.Button.Update, UiText.Button.Run, UiText.Button.Cancel);

            switch (choice)
            {
                case UiText.Button.UpdateAndRun:
                    await UpdateCustomAppAsync(node, true);
                    break;
                case UiText.Button.Update:
                    await UpdateCustomAppAsync(node, false);
                    break;
                case UiText.Button.Run:
                    await TryLaunchWithArgsAsync(node);
                    break;
            }

            return;
        }

        var appsBaseDir = AppDeployService.AppsDir;

        if (node.IsPlugin)
        {
            var marker = PluginService.GetMarkerFile(node.SectionName);

            if (File.Exists(marker) && IsAppUpToDate(marker, node.UpdateDate))
                return;

            if (node.IsQueued)
            {
                var remove = await ShowDialog(
                    node, node.Name,
                    string.Format(UiText.Dialog.MainQueuedRemoveFormat, node.Name),
                    UiText.Button.RemoveFromQueue, UiText.Button.Cancel);
                if (remove != UiText.Button.RemoveFromQueue)
                    return;
                _installQueue.Remove(node);
                if (_installQueue.IsRunning && node == _installQueue.ActiveNode && _installQueue.Cts != null)
                    await _installQueue.Cts.CancelAsync();
                else if (node.IsInstalled)
                    await SilentUninstallAsync(node, appsBaseDir);
                return;
            }

            if (_installQueue.IsRunning)
            {
                if (node == _installQueue.ActiveNode)
                {
                    var cancel = await ShowDialog(
                        node, node.Name,
                        string.Format(UiText.Dialog.MainCancelInstallInProgressFormat, node.Name),
                        UiText.Button.CancelInstallation, UiText.Button.KeepRunning);
                    if (cancel != UiText.Button.CancelInstallation)
                        return;
                    await CancelInstallAsync();
                    if (node.IsInstalled)
                        await SilentUninstallAsync(node, appsBaseDir);
                    return;
                }

                if (ctrlHeld)
                {
                    _installQueue.Enqueue(node, false);
                    if (_installQueue.IsRunning || !_installQueue.TryDequeue(out var nextCtrlNode, out var nextCtrlLaunch))
                        return;
                    _ = _installer.InstallAsync(nextCtrlNode, appsBaseDir, nextCtrlLaunch);
                    return;
                }

                var action = File.Exists(marker) ? UiText.Dialog.MainVerbUpdate : UiText.Dialog.MainVerbInstall;
                var queue = await ShowDialog(
                    node, node.Name,
                    string.Format(UiText.Dialog.MainAddToQueueFormat, node.Name, action),
                    UiText.Button.AddToQueue, UiText.Button.Cancel);
                if (queue != UiText.Button.AddToQueue)
                    return;
                _installQueue.Enqueue(node, false);
                if (_installQueue.IsRunning || !_installQueue.TryDequeue(out var nextNode, out var nextLaunch))
                    return;
                _ = _installer.InstallAsync(nextNode, appsBaseDir, nextLaunch);
                return;
            }

            if (File.Exists(marker))
            {
                if (ctrlHeld)
                {
                    await _installer.InstallAsync(node, appsBaseDir, false);
                    return;
                }

                var choice = await ShowDialog(
                    node, string.Format(UiText.Dialog.MainUpdateAvailableFormat, node.Name),
                    string.Format(UiText.Dialog.MainUpdateAvailableBodyFormat, node.Name),
                    UiText.Button.Update, UiText.Button.Cancel);
                if (choice == UiText.Button.Update)
                    await _installer.InstallAsync(node, appsBaseDir, false);
            }
            else
            {
                if (!await CheckSecurityNoticeAsync(node))
                    return;

                if (ctrlHeld)
                {
                    await _installer.InstallAsync(node, appsBaseDir, false);
                    return;
                }

                var choice = await ShowDialog(
                    node, node.Name,
                    BuildInstallPrompt(node, appsBaseDir),
                    UiText.Button.Install, UiText.Button.Cancel);
                if (choice == UiText.Button.Install)
                    await _installer.InstallAsync(node, appsBaseDir, false);
            }

            return;
        }

        var appDir = Path.Combine(appsBaseDir, node.SectionName);
        var (appExe, candidates) = AppExecutableService.Resolve(appDir, node.SectionName);

        if (appExe == null && candidates.Length > 0)
        {
            appExe = await PickExeAsync(node, appDir, candidates);
            if (appExe == null)
                return;
        }

        if (appExe != null && IsAppUpToDate(appExe, node.UpdateDate))
        {
            await TryLaunchWithArgsAsync(node);
            return;
        }

        if (node.IsQueued)
        {
            var remove = await ShowDialog(
                node, node.Name,
                string.Format(UiText.Dialog.MainQueuedRemoveFormat, node.Name),
                UiText.Button.RemoveFromQueue, UiText.Button.Cancel);
            if (remove != UiText.Button.RemoveFromQueue)
                return;

            _installQueue.Remove(node);
            if (_installQueue.IsRunning && node == _installQueue.ActiveNode && _installQueue.Cts != null)
                await _installQueue.Cts.CancelAsync();
            else if (node.IsInstalled)
                await SilentUninstallAsync(node, appsBaseDir);
            return;
        }

        if (_installQueue.IsRunning)
        {
            if (node == _installQueue.ActiveNode)
            {
                var cancel = await ShowDialog(
                    node, node.Name,
                    string.Format(UiText.Dialog.MainCancelInstallInProgressFormat, node.Name),
                    UiText.Button.CancelInstallation, UiText.Button.KeepRunning);
                if (cancel != UiText.Button.CancelInstallation)
                    return;
                await CancelInstallAsync();
                if (node.IsInstalled)
                    await SilentUninstallAsync(node, appsBaseDir);
                return;
            }

            if (ctrlHeld)
            {
                _installQueue.Enqueue(node, false);
                if (_installQueue.IsRunning || !_installQueue.TryDequeue(out var nextCtrlNode, out var nextCtrlLaunch))
                    return;
                _ = _installer.InstallAsync(nextCtrlNode, appsBaseDir, nextCtrlLaunch);
                return;
            }

            var action = appExe != null ? UiText.Dialog.MainVerbUpdate : UiText.Dialog.MainVerbInstall;
            var queue = await ShowDialog(
                node, node.Name,
                string.Format(UiText.Dialog.MainAddToQueueFormat, node.Name, action),
                UiText.Button.AddToQueue, UiText.Button.Cancel);
            if (queue != UiText.Button.AddToQueue)
                return;
            _installQueue.Enqueue(node, false);
            if (_installQueue.IsRunning || !_installQueue.TryDequeue(out var nextNode, out var nextLaunch))
                return;
            _ = _installer.InstallAsync(nextNode, appsBaseDir, nextLaunch);
            return;
        }

        if (appExe != null)
        {
            if (ctrlHeld)
            {
                await _installer.InstallAsync(node, appsBaseDir, false);
                return;
            }

            var choice = await ShowDialog(
                node, string.Format(UiText.Dialog.MainUpdateAvailableFormat, node.Name),
                string.Format(UiText.Dialog.MainUpdateAvailableBodyFormat, node.Name),
                UiText.Button.UpdateAndRun, UiText.Button.Update, UiText.Button.Run, UiText.Button.Cancel);

            switch (choice)
            {
                case UiText.Button.UpdateAndRun:
                    await _installer.InstallAsync(node, appsBaseDir, true);
                    break;
                case UiText.Button.Update:
                    await _installer.InstallAsync(node, appsBaseDir, false);
                    break;
                case UiText.Button.Run:
                    await TryLaunchWithArgsAsync(node);
                    break;
            }

            return;
        }

        if (!await CheckSecurityNoticeAsync(node))
            return;

        if (ctrlHeld)
        {
            await _installer.InstallAsync(node, appsBaseDir, false);
            return;
        }

        var installChoice = await ShowDialog(
            node, node.Name,
            BuildInstallPrompt(node, appsBaseDir),
            UiText.Button.Install, UiText.Button.InstallAndRun, UiText.Button.Cancel);

        switch (installChoice)
        {
            case UiText.Button.Install:
                await _installer.InstallAsync(node, appsBaseDir, false);
                break;
            case UiText.Button.InstallAndRun:
                await _installer.InstallAsync(node, appsBaseDir, true);
                break;
        }
    }

    private void OnMenuInstallRun(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            _ = _installer.InstallAsync(node, AppDeployService.AppsDir, true);
    }

    private void OnMenuInstall(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            _ = _installer.InstallAsync(node, AppDeployService.AppsDir, false);
    }

    private void OnMenuUpdateRun(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is not { } node)
            return;
        _ = node.IsCustom
            ? UpdateCustomAppAsync(node, true)
            : _installer.InstallAsync(node, AppDeployService.AppsDir, true);
    }

    private void OnMenuUpdate(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is not { } node)
            return;
        _ = node.IsCustom
            ? UpdateCustomAppAsync(node, false)
            : _installer.InstallAsync(node, AppDeployService.AppsDir, false);
    }

    private async Task UpdateCustomAppAsync(AppNode node, bool launchAfter)
    {
        try
        {
            var ok = await CustomAppUpdater.UpdateAsync(node, ConfirmDownloadHashMismatchAsync, this, _cts.Token);
            if (ok && launchAfter)
                await TryLaunchWithArgsAsync(node);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.CustomAppUpdateFailedFormat, node.SectionName, ex.Message));
        }
    }

    private async Task UpdateCustomAppsSequentiallyAsync(IReadOnlyList<AppNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (_cts.IsCancellationRequested)
                return;
            await UpdateCustomAppAsync(node, false);
        }
    }

    private void OnMenuAddToQueue(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is not { } node)
            return;
        _installQueue.Enqueue(node, false);
        if (_installQueue.IsRunning || !_installQueue.TryDequeue(out var nextNode, out var nextLaunch))
            return;
        _ = _installer.InstallAsync(nextNode, AppDeployService.AppsDir, nextLaunch);
    }

    private void OnMenuRemoveFromQueue(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            _installQueue.Remove(node);
    }

    private async void OnMenuCancelInstall(object? sender, RoutedEventArgs e)
    {
        try
        {
            await CancelInstallAsync();
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.MenuCancelInstallFailedFormat, ex.Message));
        }
    }

    private async Task CancelInstallAsync()
    {
        if (_installQueue.ActiveNode == null || _installQueue.Cts == null)
            return;

        if (_installQueue.InSetupPhase)
        {
            var watchCts = new CancellationTokenSource();
            var watchToken = watchCts.Token;
            var dialog = new AppDialog(
                    UiText.Dialog.MainInstallInProgressTitle,
                    string.Format(UiText.Dialog.MainCancelInstallActiveFormat, _installQueue.ActiveNode.Name) +
                    UiText.Dialog.MainCancelInstallBody,
                    UiText.Button.CancelInstallation, UiText.Button.KeepRunning)
                { Icon = new WindowIcon(_installQueue.ActiveNode.Icon) };

            var watchTask = Task.Run(async () =>
            {
                while (_installQueue.InSetupPhase && !watchToken.IsCancellationRequested)
                    await Task.Delay(100, watchToken);
                if (!watchToken.IsCancellationRequested)
                    await Dispatcher.UIThread.InvokeAsync(dialog.Close);
            }, watchToken);

            await dialog.ShowDialog(this);
            await watchCts.CancelAsync();
            await watchTask;
            watchCts.Dispose();

            if (dialog.Result != UiText.Button.CancelInstallation)
                return;
        }

        await _installQueue.Cts.CancelAsync();
    }

    private async void OnMenuRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { } node)
                return;
            await TryLaunchWithArgsAsync(node);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.MenuRunFailedFormat, ex.Message));
        }
    }

    private async void OnMenuTerminate(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { } node)
                return;
            if (RunningAppsService.GetKillCandidates(node.SectionName).Count == 0)
                return;

            var dialog = new TerminateDialog(node.Name,
                                             () => [new TerminateGroupInput(node.SectionName, node.Name, node.Icon)])
            {
                Icon = new WindowIcon(node.Icon)
            };
            await dialog.ShowDialog(this);
            if (!dialog.Confirmed)
                return;

            RunningAppsService.KillPidsWithElevation(dialog.RemainingPids);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.MenuTerminateFailedFormat, ex.Message));
        }
    }

    private async void OnMenuRunWithArgs(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { } node)
                return;
            var dialog = new RunArgsDialog(node.Name, _cliAppArgs) { Icon = new WindowIcon(node.Icon) };
            await dialog.ShowDialog(this);
            if (dialog.Choice == RunArgsDialog.RunChoice.Cancel)
                return;
            string? args = null;
            if (dialog.Choice is RunArgsDialog.RunChoice.WithArgs or RunArgsDialog.RunChoice.WithArgsAsAdmin)
            {
                var converted = AppDeployService.ConvertArgsForWine(dialog.ArgsArray);
                args = RunArgsDialog.CombineArgs(converted);
            }

            node.TryBeginLaunchFx();
            if (dialog.Choice == RunArgsDialog.RunChoice.WithArgsAsAdmin)
                await Task.Run(() => RunAsAdmin(node, args));
            else if (node.IsCustom)
                RunCustomApp(node, args);
            else
                RunApp(node, AppDeployService.AppsDir, args);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.MenuRunWithArgsFailedFormat, ex.Message));
        }
    }

    private async void OnMenuUninstall(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { } node)
                return;
            var appsBaseDir = AppDeployService.AppsDir;

            // Check if Java plugins should be co-uninstalled
            List<AppNode> javaPluginsToRemove = [];
            if (node.RequiresJava && DataContext is MainViewModel vmUninstall)
            {
                var otherRequiresJava =
                    vmUninstall.AllNodes
                               .Any(n => n != node && n is { IsInstalled: true, RequiresJava: true });

                if (!otherRequiresJava)
                {
                    javaPluginsToRemove =
                        vmUninstall.AllNodes
                                   .Where(n => PluginService.IsJavaPlugin(n.SectionName) && n.IsInstalled)
                                   .ToList();
                }
            }

            var message = javaPluginsToRemove.Count > 0
                ? string.Format(UiText.Dialog.MainUninstallJavaExtraFormat, node.Name,
                                string.Join("\n", javaPluginsToRemove.Select(n => $"\u2022 {n.Name}")))
                : string.Format(UiText.Dialog.MainUninstallSimpleFormat, node.Name);

            var confirmed = await ShowDialog(node, UiText.Dialog.MainUninstallTitle, message, UiText.Button.Uninstall, UiText.Button.Cancel);
            if (confirmed != UiText.Button.Uninstall)
                return;

            var appDir = node.IsCustom
                ? Path.Combine(CustomAppService.CustomAppsDir, node.SectionName)
                : node.IsPlugin
                    ? PluginService.GetInstallDir(node.SectionName)
                    : Path.Combine(appsBaseDir, node.SectionName);

            var running = GetRunningProcessesInDir(appDir);
            if (running.Count > 0)
            {
                var names = string.Join("\n", running.Select(p => $"\u2022 {p.ProcessName}").Distinct());
                var forceQuit = await ShowDialog(
                    node, UiText.Dialog.MainAppRunningTitle,
                    string.Format(UiText.Dialog.MainAppRunningProcessesFormat, node.Name, names),
                    UiText.Button.ForceQuitUninstall, UiText.Button.Cancel);
                if (forceQuit != UiText.Button.ForceQuitUninstall)
                    return;
                foreach (var p in running)
                    try
                    {
                        p.Kill(true);
                    }
                    catch
                    {
                        /* process may have already exited before the kill request arrived */
                    }

                await Task.Delay(500);
            }

            if (node is { IsCustom: false, IsPlugin: false })
            {
                var sourceDataDir = Path.Combine(appDir, "Data");
                if (Directory.Exists(sourceDataDir))
                {
                    var doBackup = await ShowDialog(
                        node, UiText.Dialog.MainBackupUserDataTitle,
                        string.Format(UiText.Dialog.MainBackupUserDataBodyFormat, node.Name),
                        UiText.Button.SaveBackup, UiText.Button.Skip);

                    if (doBackup == UiText.Button.SaveBackup)
                    {
                        if (AppBackupService.HasBackup(node.SectionName))
                        {
                            var choice = await ShowDialog(
                                node, UiText.Dialog.MainBackupAlreadyExistsTitle,
                                string.Format(UiText.Dialog.MainBackupExistsBodyFormat, node.Name),
                                UiText.Button.KeepNew, UiText.Button.KeepExisting);

                            if (choice == UiText.Button.KeepNew)
                            {
                                AppBackupService.DeleteBackup(node.SectionName);
                                AppBackupService.MoveToBackup(sourceDataDir, node.SectionName);
                            }
                        }
                        else
                        {
                            AppBackupService.MoveToBackup(sourceDataDir, node.SectionName);
                        }
                    }
                }
            }

            try
            {
                if (Directory.Exists(appDir))
                    Directory.Delete(appDir, true);

                if (node.IsPlugin)
                {
                    CurrentAppService.Remove(node.SectionName);
                    var commonDir = PluginService.GetInstallDir();
                    if (Directory.Exists(commonDir) && !Directory.EnumerateFileSystemEntries(commonDir).Any())
                        Directory.Delete(commonDir);
                }
                else if (node.IsCustom)
                {
                    CustomAppService.DeleteData(node.SectionName);
                    if (DataContext is MainViewModel vmCustom)
                        vmCustom.RemoveCustomApp(node);
                }
                else
                {
                    AppExecutableService.Remove(node.SectionName);
                }

                if (node is { IsPlugin: false, IsCustom: false })
                {
                    node.LocalDisplayVersion = null;
                    node.LocalPackageVersion = null;
                }

                node.IsInstalled = false;
                RunningAppsService.InvalidateExeCache(node.SectionName);

                foreach (var javaNode in javaPluginsToRemove)
                {
                    var javaDir = PluginService.GetInstallDir(javaNode.SectionName);
                    if (Directory.Exists(javaDir))
                        Directory.Delete(javaDir, true);
                    CurrentAppService.Remove(javaNode.SectionName);
                    javaNode.IsInstalled = false;
                    RunningAppsService.InvalidateExeCache(javaNode.SectionName);
                }

                if (javaPluginsToRemove.Count <= 0)
                    return;

                var commonFilesDir = PluginService.GetInstallDir();
                if (Directory.Exists(commonFilesDir) && !Directory.EnumerateFileSystemEntries(commonFilesDir).Any())
                    Directory.Delete(commonFilesDir);
            }
            catch (Exception ex)
            {
                Log.Write(string.Format(LogText.Main.UninstallFailedFormat, node.SectionName, ex.Message));
                await ShowDialog(node, UiText.Dialog.MainUninstallFailedTitle, ex.Message, UiText.Button.Ok);
            }
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.MenuUninstallConfirmationFailedFormat, ex.Message));
        }
    }

    private static IReadOnlyList<Process> GetRunningProcessesInDir(string dir)
    {
        var prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var all = Process.GetProcesses();

        // Find root matches: processes that directly reference files in appDir
        var roots = new HashSet<int>();
        foreach (var p in all)
        {
            try
            {
                bool matches;
                if (OperatingSystem.IsLinux())
                {
                    var maps = File.ReadAllText($"/proc/{p.Id}/maps");
                    matches = maps.Contains(prefix, StringComparison.Ordinal);
                    if (!matches)
                    {
                        var cmdline = File.ReadAllText($"/proc/{p.Id}/cmdline").Replace('\0', ' ');
                        matches = cmdline.Contains(prefix, StringComparison.Ordinal);
                    }
                }
                else
                {
                    var exe = p.MainModule?.FileName;
                    matches = exe != null && exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                }

                if (matches)
                    roots.Add(p.Id);
            }
            catch
            {
                /* access denied or process already exited – skip */
            }
        }

        if (roots.Count == 0)
            return [];

        // On Linux, expand roots to include all descendant processes via PPID
        if (OperatingSystem.IsLinux())
        {
            var ppidMap = new Dictionary<int, int>(); // pid -> ppid
            foreach (var p in all)
            {
                try
                {
                    var status = File.ReadAllText($"/proc/{p.Id}/status");
                    var ppidLine = status.Split('\n').FirstOrDefault(l => l.StartsWith("PPid:"));
                    if (ppidLine != null && int.TryParse(ppidLine.Split(':')[1].Trim(), out var ppid))
                        ppidMap[p.Id] = ppid;
                }
                catch
                {
                    /* process may have exited between enumeration and the status read */
                }
            }

            bool changed;
            do
            {
                changed = false;
                foreach (var (pid, ppid) in ppidMap)
                {
                    if (roots.Contains(pid) || !roots.Contains(ppid))
                        continue;
                    roots.Add(pid);
                    changed = true;
                }
            } while (changed);
        }

        return all.Where(p => roots.Contains(p.Id)).ToList();
    }

    private async Task SilentUninstallAsync(AppNode node, string appsBaseDir)
    {
        try
        {
            var appDir = node.IsPlugin
                ? PluginService.GetInstallDir(node.SectionName)
                : Path.Combine(appsBaseDir, node.SectionName);

            foreach (var p in GetRunningProcessesInDir(appDir))
                try
                {
                    p.Kill(true);
                }
                catch
                {
                    /* process may have already exited before the kill request arrived */
                }

            if (Directory.Exists(appDir))
                Directory.Delete(appDir, true);
            CurrentAppService.Remove(node.SectionName);
            if (!node.IsPlugin)
            {
                node.LocalDisplayVersion = null;
                node.LocalPackageVersion = null;
            }

            node.IsInstalled = false;
            RunningAppsService.InvalidateExeCache(node.SectionName);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.UninstallFailedFormat, node.SectionName, ex.Message));
            await ShowDialog(node, UiText.Dialog.MainUninstallFailedTitle, ex.Message, UiText.Button.Ok);
        }
    }

    private async void OnMenuSettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { IsCustom: true } node)
                return;
            if (DataContext is not MainViewModel vm)
                return;

            var win = new CustomAppWindow(node, vm.Categories, vm.SubCategoriesMap);
            await win.ShowDialog(this);
            if (!win.Success)
                return;

            try
            {
                var effectiveSection = string.IsNullOrEmpty(win.SectionName) ? node.SectionName : win.SectionName;
                if (!string.Equals(effectiveSection, node.SectionName, StringComparison.Ordinal))
                    await CustomAppService.RenameSectionAsync(node.SectionName, effectiveSection);

                var iconChanged = !string.IsNullOrEmpty(win.IconSourcePath);
                await CustomAppService.UpdateAppAsync(
                    effectiveSection,
                    win.ExeFile,
                    win.Name,
                    win.Description,
                    win.Website,
                    iconChanged ? win.IconSourcePath : null,
                    win.Category,
                    win.SubCategory,
                    win.Version,
                    win.VersionSourceExe,
                    win.DisplayVersion);

                if (iconChanged && win.IconSourcePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(win.IconSourcePath);
                    }
                    catch
                    {
                        /* icon was in a temp path that may already be gone */
                    }
                }

                var entry = new AppEntry(
                    effectiveSection,
                    win.Name,
                    win.Description,
                    win.Website,
                    win.Category,
                    win.SubCategory,
                    node.JoinedDate,
                    win.DisplayVersion,
                    win.Version,
                    win.UpdateDate,
                    win.ExeFile,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);

                var icon = iconChanged
                    ? _iconManager.ReloadCustomIcon(effectiveSection)
                    : _iconManager.GetCustomIcon(effectiveSection, vm.Columns.IconSize);

                var oldUsedBytes = node.UsedBytes;
                vm.RemoveCustomApp(node);
                var newNode = vm.AddCustomApp(entry, icon);
                newNode.SetUsedBytes(oldUsedBytes);
            }
            catch (Exception ex)
            {
                Log.Write(string.Format(LogText.Main.CustomAppUpdateFailedFormat, node.SectionName, ex.Message));
                await ShowDialog(node, UiText.Dialog.MainUpdateFailedTitle, ex.Message, UiText.Button.Ok);
            }
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.MenuSettingsCustomAppEditFailedFormat, ex.Message));
        }
    }

    private void OnMenuRunAsAdmin(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (NodeFromMenu(sender) is not { } node)
            return;
        RunAsAdmin(node);
    }

    private static void RunAsAdmin(AppNode node, string? args = null)
    {
        string appExe;
        if (node.IsCustom)
        {
            appExe = Path.Combine(CustomAppService.CustomAppsDir, node.SectionName, node.DownloadFile);
        }
        else
        {
            var appDir = AppDeployService.GetInstallDir(node.SectionName);
            var (resolved, _) = AppExecutableService.Resolve(appDir, node.SectionName);
            if (resolved == null)
                return;
            appExe = resolved;
        }

        if (!File.Exists(appExe))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(appExe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = args ?? string.Empty
            });
        }
        catch
        {
            /* UAC prompt cancelled by user */
        }
    }

    private void OnMenuOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is not { } node)
            return;
        var dir = node.IsCustom
            ? Path.Combine(CustomAppService.CustomAppsDir, node.SectionName)
            : AppDeployService.GetInstallDir(node.SectionName);
        if (!Directory.Exists(dir))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            /* shell may refuse to open the folder on this platform */
        }
    }

    private void OnMenuVisitWebsite(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is not { } node || string.IsNullOrEmpty(node.Website))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(node.Website) { UseShellExecute = true });
        }
        catch
        {
            /* default browser may not be configured */
        }
    }

    private async void OnMenuPreview(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { } node)
                return;
            var preview = await _iconManager.GetPreviewAsync(node.SectionName, _cts.Token);
            if (preview is null)
            {
                var msg = new AppDialog(UiText.Dialog.MainNoPreviewTitle, string.Format(UiText.Dialog.MainNoPreviewBodyFormat, node.Name), UiText.Button.Ok);
                await msg.ShowDialog(this);
                return;
            }

            var dialog = new AppPreviewDialog(node.Name, preview) { Icon = new WindowIcon(node.Icon) };
            await dialog.ShowDialog(this);
        }
        catch
        {
            /* preview image unavailable or window is closing */
        }
    }

    private async void OnMenuAppDatabaseEntry(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { } node)
                return;
            if (!node.IsCustom)
                await _iconManager.EnsureIconAsync(node.SectionName, 128, _cts.Token);
            var dialog = new AppProperties(node, _iconManager) { Icon = new WindowIcon(node.Icon) };
            await dialog.ShowDialog(this);
        }
        catch
        {
            /* window may be closing or node data is invalid */
        }
    }

    private async void OnMenuVirusTotal(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { } node)
                return;
            var dialog = new VirusTotalDialog(node) { Icon = new WindowIcon(node.Icon) };
            await dialog.ShowDialog(this);
        }
        catch
        {
            /* window may be closing or node data is invalid */
        }
    }

    private static AppNode? NodeFromMenu(object? sender)
    {
        return (sender as MenuItem)?.FindAncestorOfType<ContextMenu>()?.DataContext as AppNode;
    }

    private static void RunApp(AppNode node, string appsBaseDir, string? args = null)
    {
        var appDir = Path.Combine(appsBaseDir, node.SectionName);
        var (appExe, _) = AppExecutableService.Resolve(appDir, node.SectionName);
        if (appExe != null)
            AppDeployService.LaunchApp(appExe, args);
    }

    private static void RunCustomApp(AppNode node, string? args = null)
    {
        var appExe = Path.Combine(CustomAppService.CustomAppsDir, node.SectionName, node.DownloadFile);
        if (File.Exists(appExe))
            AppDeployService.LaunchApp(appExe, args);
    }

    private async Task TryLaunchWithArgsAsync(AppNode node)
    {
        if (!node.TryBeginLaunchFx())
            return;

        if (OperatingSystem.IsLinux() && !node.IsCustom && !await EnsureWineReadyAsync())
            return;

        if (OperatingSystem.IsLinux() && !node.IsCustom)
            await WinePrefixTheme.ApplyAsync(Application.Current?.ActualThemeVariant == ThemeVariant.Dark);

        if (_cliAppArgs.Length > 0)
        {
            var dialog = new RunArgsDialog(node.Name, _cliAppArgs) { Icon = new WindowIcon(node.Icon) };
            await dialog.ShowDialog(this);
            if (dialog.Choice == RunArgsDialog.RunChoice.Cancel)
                return;
            string? args = null;
            if (dialog.Choice is RunArgsDialog.RunChoice.WithArgs or RunArgsDialog.RunChoice.WithArgsAsAdmin)
            {
                var converted = AppDeployService.ConvertArgsForWine(dialog.ArgsArray);
                args = RunArgsDialog.CombineArgs(converted);
            }

            if (dialog.Choice == RunArgsDialog.RunChoice.WithArgsAsAdmin)
                await Task.Run(() => RunAsAdmin(node, args));
            else if (node.IsCustom)
                RunCustomApp(node, args);
            else
                RunApp(node, AppDeployService.AppsDir, args);
        }
        else
        {
            if (node.IsCustom)
                RunCustomApp(node);
            else
                RunApp(node, AppDeployService.AppsDir);
        }
    }

    private static async Task ScanAndCacheNodeSizeAsync(AppNode node, string appDir)
    {
        var bytes = await Task.Run(() => AppDiskUsageService.GetDirectorySize(appDir));
        var cache = AppDiskUsageService.LoadCache();
        cache.Sizes[node.SectionName] = bytes;
        AppDiskUsageService.SaveCache(cache);
        await Dispatcher.UIThread.InvokeAsync(() => node.SetUsedBytes(bytes));
    }

    private async Task<string?> ResolveAppExeAsync(AppNode node, string appsBaseDir)
    {
        var appDir = Path.Combine(appsBaseDir, node.SectionName);
        var (exePath, candidates) = AppExecutableService.Resolve(appDir, node.SectionName);
        if (exePath != null)
            return exePath;
        if (candidates.Length == 0)
            return null;
        return await PickExeAsync(node, appDir, candidates);
    }

    private async Task<string?> PickExeAsync(AppNode node, string appDir, string[] candidates)
    {
        var dialog = new ExePickerDialog(node.Name, candidates) { Icon = new WindowIcon(node.Icon) };
        await dialog.ShowDialog(this);
        if (dialog.SelectedExe == null)
            return null;
        AppExecutableService.Save(node.SectionName, dialog.SelectedExe, node.SectionName + ".exe");
        return Path.Combine(appDir, dialog.SelectedExe);
    }

    private void OnAnyRunningChanged(object? sender, string sectionName)
    {
        Dispatcher.UIThread.Post(UpdateTerminateAllButton);
    }

    private void UpdateTerminateAllButton()
    {
        if (DataContext is not MainViewModel vm)
        {
            TerminateAllButton.IsVisible = false;
            TerminateAllCount.Text = "";
            return;
        }

        var count = vm.AllNodes.Count(n => !n.IsPlugin && RunningAppsService.IsRunning(n.SectionName));
        TerminateAllButton.IsVisible = count > 0;
        TerminateAllBadge.IsVisible = count > 1;
        TerminateAllCount.Text = count.ToString();
    }

    private async void OnTerminateAllApps(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm)
                return;
            if (!vm.AllNodes.Any(n => n is { IsRunning: true, IsPlugin: false } &&
                                      RunningAppsService.GetKillCandidates(n.SectionName).Count > 0))
                return;

            var dialog = new TerminateDialog(UiText.Dialog.TerminateAllAppsName,
                                             () => vm.AllNodes
                                                     .Where(n => n is { IsPlugin: false } && RunningAppsService.IsRunning(n.SectionName))
                                                     .Select(n => new TerminateGroupInput(n.SectionName, n.Name, n.Icon))
                                                     .ToList());
            await dialog.ShowDialog(this);
            if (!dialog.Confirmed)
                return;

            RunningAppsService.KillPidsWithElevation(dialog.RemainingPids);
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.MenuTerminateFailedFormat, ex.Message));
        }
    }

    private async void OnLinuxSetupButton(object? sender, RoutedEventArgs e)
    {
        try
        {
            await new LinuxSetupDialog { Icon = Icon }.ShowDialog(this);
            await CheckWineUpdateAsync();
        }
        catch
        {
            /* window may be closing */
        }
    }

    private async Task CheckWineUpdateAsync()
    {
        try
        {
            var hasUpdate = await WineRunnersClient.HasLatestUpdateAsync();
            Dispatcher.UIThread.Post(() => LinuxSetupButton.Classes.Set("update", hasUpdate));
        }
        catch
        {
            // Update check is best-effort — a failed network probe just leaves the button unstyled.
        }
    }

    /// Ensures a working Wine setup before running an installer or launching a .exe.
    /// First call ever prompts the user; later only when no Wine is available.
    /// Returns true when Wine is ready, false when the user cancelled or setup failed.
    private async Task<bool> EnsureWineReadyAsync()
    {
        if (!OperatingSystem.IsLinux())
            return true;
        var settings = SettingsService.Load();
        if (!settings.LinuxSetupCompleted || !WineService.IsWineReady())
        {
            try
            {
                var dialog = new LinuxSetupDialog { Icon = Icon };
                await dialog.ShowDialog(this);
            }
            catch
            {
                /* window may be closing */
            }

            if (!WineService.IsWineReady())
                return false;
        }

        try
        {
            await WineService.EnsurePrefixReadyAsync();
        }
        catch (Exception ex)
        {
            await ShowDialog(
                UiText.Dialog.LinuxSetupTitle,
                string.Format(UiText.Dialog.LinuxSetupFailedFormat, ex.Message),
                UiText.Button.Ok);
            return false;
        }

        return true;
    }

    private async void OnImportApp(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm)
                return;

            var source = await ShowDialog(
                UiText.Dialog.MainImportFromTitle,
                UiText.Dialog.MainImportFromBody,
                UiText.Button.ImportLocal, UiText.Button.ImportGitHub, UiText.Button.Cancel);
            if (source != UiText.Button.ImportLocal && source != UiText.Button.ImportGitHub)
                return;

            string? presetFolder = null;
            string? presetVersion = null;
            string? presetDisplayVersion = null;
            string? presetUpdateDate = null;
            string? presetUpdateUrl = null;
            string? presetUpdateFile = null;
            DateTime? presetUpdateFileMtime = null;
            if (source == UiText.Button.ImportGitHub)
            {
                var gh = new GitHubImportDialog(ConfirmDownloadHashMismatchAsync);
                await gh.ShowDialog(this);
                if (!gh.Success)
                    return;
                presetFolder = gh.ExtractedFolder;
                presetVersion = gh.Version;
                presetDisplayVersion = gh.DisplayVersion;
                presetUpdateDate = gh.UpdateDate;
                presetUpdateUrl = gh.UpdateUrl;
                presetUpdateFile = gh.UpdateFile;
                presetUpdateFileMtime = gh.UpdateFileMtime;
            }

            var win = new CustomAppWindow(
                vm.Categories, vm.SubCategoriesMap,
                presetFolder, presetVersion, presetDisplayVersion, presetUpdateDate,
                presetUpdateUrl, presetUpdateFile, presetUpdateFileMtime);
            await win.ShowDialog(this);
            if (!win.Success)
                return;

            try
            {
                var isInPlace = CustomAppService.IsDirectChildOfCustomApps(win.FolderName);
                var mode = ImportMode.Copy;
                if (!isInPlace)
                {
                    var choice = await ShowDialog(
                        UiText.Dialog.MainImportMoveOrCopyTitle,
                        string.Format(UiText.Dialog.MainImportMoveOrCopyBodyFormat, win.Name),
                        UiText.Button.Move, UiText.Button.Copy, UiText.Button.Cancel);
                    switch (choice)
                    {
                        case UiText.Button.Move:
                            mode = ImportMode.Move;
                            break;
                        case UiText.Button.Copy:
                            mode = ImportMode.Copy;
                            break;
                        default:
                            return;
                    }
                }

                if (!isInPlace && mode == ImportMode.Copy)
                {
                    while (true)
                    {
                        var sourceSize = await Task.Run(() => AppDiskUsageService.GetDirectorySize(win.FolderName));
                        var required = (long)(sourceSize * 1.1);
                        var available = AppDiskUsageService.GetAvailableFreeSpace(CustomAppService.CustomAppsDir);
                        if (available < required)
                        {
                            var choice = await ShowDiskSpaceDialog(null, win.Name, required, available);
                            if (choice == UiText.Button.Retry)
                                continue;
                            return;
                        }

                        break;
                    }
                }

                ImportResult importResult;
                if (isInPlace)
                {
                    importResult = await CustomAppService.ImportAppAsync(
                        win.FolderName,
                        win.ExeFile,
                        win.Name,
                        win.Description,
                        win.Website,
                        win.IconSourcePath,
                        win.Category,
                        win.SubCategory,
                        win.Version,
                        win.VersionSourceExe,
                        win.DisplayVersion,
                        mode: ImportMode.Move,
                        preferredFolderName: win.SectionName,
                        updateUrl: win.UpdateUrl,
                        updateFile: win.UpdateFile,
                        updateFileMtime: win.UpdateFileMtime);
                }
                else
                {
                    var copyDialog = new CopyProgressDialog(win.FolderName);
                    var copyProgress = new Progress<CopyProgress>(copyDialog.Report);
                    var importTask = CustomAppService.ImportAppAsync(
                        win.FolderName,
                        win.ExeFile,
                        win.Name,
                        win.Description,
                        win.Website,
                        win.IconSourcePath,
                        win.Category,
                        win.SubCategory,
                        win.Version,
                        win.VersionSourceExe,
                        win.DisplayVersion,
                        copyProgress,
                        copyDialog.CancellationToken,
                        mode,
                        win.SectionName,
                        win.UpdateUrl,
                        win.UpdateFile,
                        win.UpdateFileMtime);
                    _ = importTask.ContinueWith(t =>
                                                    Dispatcher.UIThread.Post(t.IsCompletedSuccessfully
                                                                                 ? copyDialog.NotifyDone
                                                                                 : copyDialog.Close));
                    await copyDialog.ShowDialog(this);
                    importResult = await importTask;
                }

                var folderName = importResult.FolderName;
                if (importResult.SourceDeleteError is { } deleteError)
                {
                    Log.Write(string.Format(LogText.Main.ImportSourceDeleteFailedFormat, win.Name, deleteError));
                    await ShowDialog(
                        UiText.Dialog.MainImportSourceDeleteFailedTitle,
                        string.Format(UiText.Dialog.MainImportSourceDeleteFailedFormat, win.Name, deleteError),
                        UiText.Button.Ok);
                }

                // Remove temp icon file created by the gallery picker
                var iconPath = win.IconSourcePath;
                if (iconPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(iconPath);
                    }
                    catch
                    {
                        /* file may already be gone */
                    }
                }

                var entry = new AppEntry(
                    folderName,
                    win.Name,
                    win.Description,
                    win.Website,
                    win.Category,
                    win.SubCategory,
                    win.JoinedDate,
                    win.DisplayVersion,
                    win.Version,
                    win.UpdateDate,
                    win.ExeFile,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);

                var icon = _iconManager.GetCustomIcon(folderName, vm.Columns.IconSize);
                var newNode = vm.AddCustomApp(entry, icon);
                var appDir = Path.Combine(CustomAppService.CustomAppsDir, folderName);
                _ = ScanAndCacheNodeSizeAsync(newNode, appDir);
            }
            catch (OperationCanceledException)
            {
                /* user cancelled the import – partial files already cleaned up */
            }
            catch (Exception ex)
            {
                Log.Write(string.Format(LogText.Main.AddCustomAppFailedFormat, ex.Message));
                await ShowDialog(UiText.Dialog.MainAddCustomAppFailed, ex.Message, UiText.Button.Ok);
            }
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.MenuAddImportDialogFailedFormat, ex.Message));
        }
    }


    private void ShowDownloadBar(bool visible)
    {
        DownloadBar.IsVisible = visible;
        DownloadProgressBar.IsIndeterminate = visible;
        if (visible)
            return;
        DownloadProgressBar.Value = 0;
        DownloadSizeText.Text = string.Empty;
        DownloadSpeedText.Text = string.Empty;
    }

    private void SetInstalling(bool value)
    {
        if (DataContext is MainViewModel vm)
            vm.Columns.IsInstalling = value;
    }

    private static bool IsAppUpToDate(string exePath, string updateDate)
    {
        if (!DateTime.TryParse(updateDate, out var date))
            return true;
        return File.GetLastWriteTime(exePath).Date >= date.Date;
    }

    private async Task<string?> ShowDialog(string title, string message, params string[] buttons)
    {
        var dialog = new AppDialog(title, message, buttons);
        await dialog.ShowDialog(this);
        return dialog.Result;
    }

    private Task<string?> ShowDiskSpaceDialog(AppNode? node, string appName, long required, long available)
    {
        var msg = string.Format(UiText.Dialog.MainNotEnoughSpaceBody, appName,
                                AppDiskUsageService.FormatSize(required), AppDiskUsageService.FormatSize(available));
        var title = string.Format(UiText.Dialog.MainNotEnoughSpaceTitleFormat, appName);
        return node != null
            ? ShowDialog(node, title, msg, UiText.Button.Retry, UiText.Button.Cancel)
            : ShowDialog(title, msg, UiText.Button.Retry, UiText.Button.Cancel);
    }

    private async Task<bool> CheckSecurityNoticeAsync(AppNode node)
    {
        var notice = SecurityNoticeService.Resolve(node.SectionName);
        if (notice == null)
            return true;

        var altNames = notice.Alternatives;
        if (notice.Alternatives.Count > 0 && DataContext is MainViewModel vm)
        {
            var nodeMap = vm.AllNodes
                            .GroupBy(n => n.SectionName, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            altNames = notice.Alternatives
                             .Select(key => nodeMap.GetValueOrDefault(key, key))
                             .ToArray();
        }

        var dlg = new SecurityNoticeDialog(notice, altNames)
        {
            Title = string.Format(UiText.Dialog.MainSecurityNoticeTitleFormat, node.Name),
            Icon = new WindowIcon(node.Icon)
        };
        await dlg.ShowDialog(this);
        return dlg.Proceeded;
    }

    private async Task<string?> ShowDialog(AppNode node, string title, string message, params string[] buttons)
    {
        var dialog = new AppDialog(title, message, buttons)
        {
            Icon = new WindowIcon(node.Icon)
        };
        await dialog.ShowDialog(this);
        return dialog.Result;
    }

    private async Task<bool> ConfirmDownloadHashMismatchAsync(
        string sectionName, string displayName, string downloadFile, string sha256, string filePath)
    {
        if (DataContext is not MainViewModel vm)
            return false;

        var entry = new AppEntry(
            sectionName, displayName, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty,
            DateTime.Today.ToString("yyyy-MM-dd"),
            downloadFile, sha256, string.Empty, string.Empty, string.Empty, string.Empty);
        var node = new AppNode(entry, _iconManager.GetIcon(sectionName, vm.Columns.IconSize), vm.Columns);

        var choice = await ShowDialog(
            UiText.Dialog.InstallHashMismatchTitle, UiText.Dialog.InstallHashMismatchBody,
            UiText.Button.ScanWithVirusTotal, UiText.Button.ProceedAnyway, UiText.Button.Cancel);
        if (choice == UiText.Button.ScanWithVirusTotal)
        {
            await new VirusTotalDialog(node, filePath) { Icon = Icon }.ShowDialog(this);
            choice = await ShowDialog(
                UiText.Dialog.InstallHashMismatchTitle, UiText.Dialog.InstallHashProceedQuestion,
                UiText.Button.Proceed, UiText.Button.Cancel);
            return choice == UiText.Button.Proceed;
        }

        return choice == UiText.Button.ProceedAnyway;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _themeController.Init();
        _selfUpdate = new SelfUpdateCoordinator(_cts.Token);
        UpdateIconSizeButton();
        UpdateCategoryScopeButton();
        UpdateCategoryDisplayButton();
        UpdateInstallFilterButton();
        UpdateViewModeButton();
        UpdateFontSizeButton();
        _ = CheckForUpdateAsync();
        DataContextChanged += (_, _) =>
        {
            UpdateIconSizeButton();
            UpdateCategoryScopeButton();
            UpdateCategoryDisplayButton();
            UpdateInstallFilterButton();
            UpdateViewModeButton();
            UpdateFontSizeButton();
            RefreshUpdateButton();
        };
        Closing += OnWindowClosing;
        MainList.AttachedToVisualTree += (_, _) =>
        {
            var s = ListScroller;
            if (s != null) s.ScrollChanged += (_, _) => UpdateStickyHeader();
        };
        MainGridList.AttachedToVisualTree += (_, _) =>
        {
            var s = GridScroller;
            if (s != null) s.ScrollChanged += (_, _) => UpdateStickyHeader();
        };
        KeyDown += OnWindowKeyDown;
        SearchBox.AddHandler(KeyDownEvent, OnSearchPreviewKeyDown, RoutingStrategies.Tunnel);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));
        _searchHistory = SearchHistoryService.Load();
        _ipcServer = new IpcServer(Program.PipeName,
                                   args => Dispatcher.UIThread.Post(() => OnIpcArgsReceived(args)));
        _ipcServer.Start();
    }

    private void OnIpcArgsReceived(string[] args)
    {
        _ipcArgBatch.AddRange(args);
        _ipcDebounceCts?.Cancel();
        _ipcDebounceCts = new CancellationTokenSource();
        var debounceToken = _ipcDebounceCts.Token;
        Task.Delay(500, debounceToken).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Dispatcher.UIThread.Post(FlushIpcBatch);
        }, TaskScheduler.Default);
    }

    private async void FlushIpcBatch()
    {
        try
        {
            var args = _ipcArgBatch.ToArray();
            _ipcArgBatch.Clear();
            _cliAppArgs = args;
            Activate();
            Win32Window.BringToForeground(this);
            const int maxDisplay = 5;
            var preview = args.Length <= maxDisplay
                ? string.Join("\n", args)
                : string.Join("\n", args.Take(maxDisplay)) + string.Format(UiText.Dialog.MainIpcArgsMoreFormat, args.Length - maxDisplay);
            await ShowDialog(UiText.Dialog.MainArgsUpdatedTitle,
                             string.Format(UiText.Dialog.MainIpcArgsBodyFormat, preview),
                             UiText.Button.Ok);
        }
        catch
        {
            /* window may be closing or dialog failed to show */
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is not { Key: Key.F, KeyModifiers: KeyModifiers.Control })
            return;
        SearchBox.Focus();
        e.Handled = true;
    }

    private async void OnTipsButton(object? sender, RoutedEventArgs e)
    {
        try
        {
            await new TipsDialog { Icon = Icon }.ShowDialog(this);
        }
        catch
        {
            /* window may be closing */
        }
    }

    private void OnThemePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right)
            _themeController.Toggle();
    }

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        _themeController.Toggle();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _themeController.ApplyDarkTitlebar(Application.Current?.ActualThemeVariant == ThemeVariant.Dark);
        RunningAppsService.Start();
        RunningAppsService.Changed += OnAnyRunningChanged;
        UpdateTerminateAllButton();
        _ = CheckOrphanedFilesAsync();
        if (OperatingSystem.IsLinux())
        {
            LinuxSetupButton.IsVisible = true;
            _ = CheckWineUpdateAsync();
        }

        var settings = SettingsService.Load();
        if (settings.HasShownTips)
            return;
        settings.HasShownTips = true;
        SettingsService.Save(settings);
        ShowTipsWhenIdle();
    }

    private void ShowTipsWhenIdle()
    {
        LayoutUpdated += OnLayoutUpdated;
        return;

        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
                return;
            LayoutUpdated -= OnLayoutUpdated;
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await Task.Delay(1000);
                try
                {
                    await new TipsDialog { Icon = Icon }.ShowDialog(this);
                }
                catch
                {
                    /* window may be closing before the dialog opens */
                }
            }, DispatcherPriority.Background);
        }
    }

    private async Task CheckOrphanedFilesAsync()
    {
        var appsDir = AppDeployService.AppsDir;
        if (!Directory.Exists(appsDir))
            return;

        var orphans = await Task.Run(() =>
        {
            var badFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "App", "Data", "Other" };
            var found = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(appsDir, "*", SearchOption.TopDirectoryOnly))
            {
                if (Directory.Exists(entry))
                {
                    if (badFolders.Contains(Path.GetFileName(entry)))
                        found.Add(entry);
                }
                else
                {
                    found.Add(entry);
                }
            }

            return found
                   .OrderBy(p => Directory.Exists(p) ? 0 : 1)
                   .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                   .ToList();
        });

        if (orphans.Count == 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dlg = new LeftoverFilesDialog(orphans) { Icon = Icon };
            await dlg.ShowDialog(this);
        }, DispatcherPriority.Background);
    }

    private void OnIconSizeCycle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.Columns.CycleIconSize();
        UpdateIconSizeButton();
        _ = ReloadIconsForSizeAsync(vm);
    }

    private void OnIconSizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || DataContext is not MainViewModel vm)
            return;
        vm.Columns.CycleIconSize(true);
        UpdateIconSizeButton();
        _ = ReloadIconsForSizeAsync(vm);
    }

    private async Task ReloadIconsForSizeAsync(MainViewModel vm)
    {
        var size = vm.Columns.IconSize;
        foreach (var node in vm.AllNodes)
            node.Icon = node.IsCustom
                ? _iconManager.GetCustomIcon(node.SectionName, size)
                : _iconManager.GetIcon(node.SectionName, size);
        _iconManager.EvictIconsForOtherSizes(size);
        await StartIconDownloadsAsync(vm);
    }

    private void UpdateIconSizeButton()
    {
        if (DataContext is not MainViewModel vm)
            return;
        IconSizeButton.Content = $"{vm.Columns.IconSize}px";
    }

    private void SubscribeViewModel(MainViewModel vm)
    {
        var attachedNodes = new HashSet<AppNode>();

        void AttachInstallListeners()
        {
            foreach (var node in vm.AllNodes)
            {
                if (!attachedNodes.Add(node))
                    continue;
                node.PropertyChanged += (_, ev) =>
                {
                    if (ev.PropertyName == nameof(AppNode.IsInstalled))
                        Dispatcher.UIThread.Post(UpdateInstallFilterButton);
                    else if (ev.PropertyName == nameof(AppNode.NeedsUpdate))
                        Dispatcher.UIThread.Post(RefreshUpdateButton);
                };
            }
        }

        AttachInstallListeners();
        vm.SetGridActive(vm.Columns.IsGridView);
        RecomputeGridColumns(vm);
        vm.Columns.PropertyChanged += (_, ev) =>
        {
            switch (ev.PropertyName)
            {
                case nameof(ColumnWidths.IsGridView):
                    vm.SetGridActive(vm.Columns.IsGridView);
                    if (vm.Columns.IsGridView)
                        RecomputeGridColumns(vm);
                    break;
                case nameof(ColumnWidths.TileWidth):
                    if (vm.Columns.IsGridView)
                        RecomputeGridColumns(vm);
                    break;
                case nameof(ColumnWidths.ShowCurrentColumn):
                    RefreshUpdateButton();
                    break;
            }
        };
        vm.PropertyChanged += (sender, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CategoryDisplay):
                    _pendingScrollTop = true;
                    break;
                case nameof(MainViewModel.CategoryScope):
                    _pendingScrollTarget = FindTopVisibleSectionName();
                    _ = StartIconDownloadsAsync(vm);
                    break;
                case nameof(MainViewModel.InstallFilter):
                    ApplyViewPreset(vm);
                    if (vm.InstallFilter == InstallFilter.Installed)
                        Dispatcher.UIThread.Post(ReclaimMemory, DispatcherPriority.Background);
                    break;
            }
        };
        vm.RowsFullyLoaded += () =>
        {
            AttachInstallListeners();
            RefreshUpdateButton();
        };
        SearchBox.AsyncPopulator = (text, _) =>
            Task.FromResult(vm.SearchAppNames(text ?? string.Empty).Cast<object>());
        vm.BeforeRebuildRows += () =>
        {
            _pendingScrollY = GetActiveScrollY();
            _pendingScrollTarget ??= FindTopVisibleSectionName();
        };
        vm.RowsFullyLoaded += () =>
        {
            if (_pendingScrollTop)
            {
                _pendingScrollTop = false;
                _pendingScrollY = null;
                _pendingScrollTarget = null;
                SetActiveScrollY(0);
            }
            else if (_pendingScrollApp != null)
            {
                var app = _pendingScrollApp;
                _pendingScrollApp = null;
                _pendingScrollY = null;
                _pendingScrollTarget = null;
                ScrollToApp(app);
            }
            else if (_pendingScrollTarget != null)
            {
                var target = _pendingScrollTarget;
                var fallbackY = _pendingScrollY;
                _pendingScrollTarget = null;
                _pendingScrollY = null;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!TryScrollToSectionName(target) && fallbackY is { } y)
                        SetActiveScrollY(y);
                }, DispatcherPriority.Background);
            }
            else if (_pendingScrollY is { } y)
            {
                _pendingScrollY = null;
                Dispatcher.UIThread.Post(() => SetActiveScrollY(y), DispatcherPriority.Background);
            }

            UpdateStickyHeader();
        };
    }

    private string? FindTopVisibleSectionName()
    {
        if (DataContext is not MainViewModel vm)
            return null;
        var list = ActiveList;
        var rows = ActiveRows(vm);
        Control? best = null;
        var bestY = double.PositiveInfinity;
        foreach (var c in list.GetRealizedContainers())
        {
            var idx = list.IndexFromContainer(c);
            if (idx < 0 || idx >= rows.Count) continue;
            var row = rows[idx];
            if (row is not AppNode && row is not GridTileRow) continue;
            var y = GetViewportY(c);
            if (y is not { } yv || yv < 0) continue;
            if (yv < bestY)
            {
                best = c;
                bestY = yv;
            }
        }

        if (best == null) return null;
        var bIdx = list.IndexFromContainer(best);
        if (bIdx < 0 || bIdx >= rows.Count) return null;
        return rows[bIdx] switch
        {
            AppNode a => a.SectionName,
            GridTileRow g => g.Tiles.FirstOrDefault()?.SectionName,
            _ => null
        };
    }

    private bool TryScrollToSectionName(string sectionName)
    {
        if (DataContext is not MainViewModel vm)
            return false;
        var node = vm.FlatRows.OfType<AppNode>().FirstOrDefault(n => n.SectionName == sectionName);
        if (node == null) return false;
        ScrollToNode(node, GetStickyOffset(vm));
        return true;
    }

    private static IList<object> ActiveRows(MainViewModel vm)
    {
        return vm.Columns.IsGridView ? vm.GridRows : vm.FlatRows;
    }

    private double GetStickyOffset(MainViewModel vm)
    {
        if (vm.CategoryDisplay == CategoryDisplayMode.None)
            return 0;
        var h = StickyHeader.Bounds.Height;
        return h > 0 ? h : 26.0;
    }

    private void UpdateStickyHeader()
    {
        if (DataContext is not MainViewModel vm || vm.CategoryDisplay == CategoryDisplayMode.None)
        {
            StickyHeader.IsVisible = false;
            return;
        }

        var list = ActiveList;
        var rows = ActiveRows(vm);
        var topRealizedIndex = -1;
        var topY = double.PositiveInfinity;
        foreach (var c in list.GetRealizedContainers())
        {
            var y = GetViewportY(c);
            if (y is not { } yv || yv < 0) continue;
            if (yv < topY)
            {
                topY = yv;
                topRealizedIndex = list.IndexFromContainer(c);
            }
        }

        if (topRealizedIndex <= 0)
        {
            StickyHeader.IsVisible = false;
            return;
        }

        string? currentCategory = null;
        for (var i = topRealizedIndex - 1; i >= 0; i--)
        {
            if (rows[i] is not CategoryNode { HasCategory: true } cat) continue;
            currentCategory = cat.Category;
            break;
        }

        if (currentCategory == null)
        {
            StickyHeader.IsVisible = false;
            return;
        }

        StickyHeaderText.Text = currentCategory;
        StickyHeader.IsVisible = true;
    }

    private void OnCategoryScopeCycle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.CategoryScope = vm.CategoryScope switch
        {
            CategoryScope.Standard => CategoryScope.Extended,
            CategoryScope.Extended => CategoryScope.Full,
            _ => CategoryScope.Standard
        };
        UpdateCategoryScopeButton();
    }

    private void OnCategoryScopePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || DataContext is not MainViewModel vm)
            return;
        vm.CategoryScope = vm.CategoryScope switch
        {
            CategoryScope.Standard => CategoryScope.Full,
            CategoryScope.Extended => CategoryScope.Standard,
            _ => CategoryScope.Extended
        };
        UpdateCategoryScopeButton();
    }

    private void UpdateCategoryScopeButton()
    {
        if (DataContext is not MainViewModel vm)
            return;
        CategoryScopeButton.Content = vm.CategoryScope switch
        {
            CategoryScope.Full => UiText.Status.CategoryScopeFull,
            CategoryScope.Extended => UiText.Status.CategoryScopeExtended,
            _ => UiText.Status.CategoryScopeStandard
        };
    }

    private void OnCategoryDisplayCycle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.CategoryDisplay = vm.CategoryDisplay switch
        {
            CategoryDisplayMode.Full => CategoryDisplayMode.Categories,
            CategoryDisplayMode.Categories => CategoryDisplayMode.None,
            _ => CategoryDisplayMode.Full
        };
        UpdateCategoryDisplayButton();
    }

    private void OnCategoryDisplayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || DataContext is not MainViewModel vm)
            return;
        vm.CategoryDisplay = vm.CategoryDisplay switch
        {
            CategoryDisplayMode.Full => CategoryDisplayMode.None,
            CategoryDisplayMode.Categories => CategoryDisplayMode.Full,
            _ => CategoryDisplayMode.Categories
        };
        UpdateCategoryDisplayButton();
    }

    private void UpdateCategoryDisplayButton()
    {
        if (DataContext is not MainViewModel vm)
            return;
        CategoryDisplayButton.Content = vm.CategoryDisplay switch
        {
            CategoryDisplayMode.Categories => UiText.Status.CategoryDisplayCategories,
            CategoryDisplayMode.None => UiText.Status.CategoryDisplayNoGroups,
            _ => UiText.Status.CategoryDisplayTree
        };
    }

    private async void OnInstallFilterCycle(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm)
                return;
            var target = vm.InstallFilter switch
            {
                InstallFilter.All => InstallFilter.Installed,
                InstallFilter.Installed => InstallFilter.NotInstalled,
                _ => InstallFilter.All
            };
            await SwitchFilterAsync(vm, target);
            UpdateInstallFilterButton();
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.InstallFilterCycleFailedFormat, ex.Message));
        }
    }

    private async void OnInstallFilterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            if (e.InitialPressMouseButton != MouseButton.Right || DataContext is not MainViewModel vm)
                return;
            var target = vm.InstallFilter switch
            {
                InstallFilter.All => InstallFilter.NotInstalled,
                InstallFilter.Installed => InstallFilter.All,
                _ => InstallFilter.Installed
            };
            await SwitchFilterAsync(vm, target);
            UpdateInstallFilterButton();
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.InstallFilterPointerReleasedFailedFormat, ex.Message));
        }
    }

    private void UpdateInstallFilterButton()
    {
        if (DataContext is not MainViewModel vm)
            return;
        InstallFilterButton.IsVisible = vm.AllNodes.Any(n => n.IsInstalled);
        InstallFilterButton.Content = vm.InstallFilter switch
        {
            InstallFilter.Installed => UiText.Status.InstallFilterInstalled,
            InstallFilter.NotInstalled => UiText.Status.InstallFilterNotInstalled,
            _ => UiText.Status.InstallFilterAll
        };
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            await _selfUpdate.CheckAsync();
        }
        catch
        {
            // best-effort: refresh update button even if self-update check fails
        }

        RefreshUpdateButton();
    }

    private void RefreshUpdateButton()
    {
        var info = _selfUpdate.Pending;
        if (info != null)
        {
            UpdateButton.IsVisible = true;
            UpdateButton.Content = string.Format(UiText.Status.UpdateVersionFormat, info.Version);
            ToolTip.SetTip(UpdateButton, UiText.Tip.UpdateApportia);
            return;
        }

        if (DataContext is MainViewModel vm && vm.AllNodes.Count(n => n.NeedsUpdate) >= 2)
        {
            UpdateButton.IsVisible = true;
            UpdateButton.Content = UiText.Status.UpdateAll;
            ToolTip.SetTip(UpdateButton, UiText.Tip.UpdateAllApps);
            return;
        }

        UpdateButton.IsVisible = false;
    }

    private void OnUpdateAllApps()
    {
        if (DataContext is not MainViewModel vm)
            return;
        var pending = vm.AllNodes.Where(n => n.NeedsUpdate).ToList();
        if (pending.Count == 0)
            return;

        var customs = pending.Where(n => n.IsCustom).ToList();
        if (customs.Count > 0)
            _ = UpdateCustomAppsSequentiallyAsync(customs);

        var regular = pending.Where(n => !n.IsCustom).ToList();
        if (regular.Count == 0)
            return;

        foreach (var node in regular.Skip(1))
            _installQueue.Enqueue(node, false);

        _ = _installer.InstallAsync(regular[0], AppDeployService.AppsDir, false);
    }

    private async void OnUpdateApp(object? sender, RoutedEventArgs e)
    {
        try
        {
            var info = _selfUpdate.Pending;
            if (info == null)
            {
                OnUpdateAllApps();
                return;
            }

            var changelog = new ChangelogDialog(info.Version, info.Changelog);
            await changelog.ShowDialog(this);
            if (!changelog.Confirmed)
                return;

            UpdateButton.IsEnabled = false;
            ShowDownloadBar(true);
            DownloadProgressBar.IsIndeterminate = false;
            DownloadSizeText.Text = string.Format(UiText.Status.DownloadingUpdateFormat, info.Version);
            DownloadSpeedText.Text = string.Empty;

            try
            {
                var progress = new Progress<int>(p =>
                {
                    DownloadProgressBar.Value = p;
                    DownloadSizeText.Text = string.Format(UiText.Status.DownloadingUpdateProgressFormat, info.Version, p);
                });
                await _selfUpdate.ApplyAsync(
                    progress,
                    (file, sha256, path) => ConfirmDownloadHashMismatchAsync("Apportia", "Apportia", file, sha256, path));
            }
            catch (Exception ex)
            {
                ShowDownloadBar(false);
                UpdateButton.IsEnabled = true;
                Log.Write(string.Format(LogText.Main.SelfUpdateFailedFormat, ex.Message));
            }
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.SelfUpdateSetupFailedFormat, ex.Message));
        }
    }

    private void OnViewModeToggle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.Columns.IsGridView = !vm.Columns.IsGridView;
        ColumnHeaderBar.IsVisible = !vm.Columns.IsGridView;
        UpdateViewModeButton();
    }

    private void OnViewModePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right)
            OnViewModeToggle(sender, e);
    }

    private void UpdateViewModeButton()
    {
        if (DataContext is not MainViewModel vm)
            return;
        ViewModeButton.Content = vm.Columns.IsGridView ? UiText.Status.ViewModeGrid : UiText.Status.ViewModeList;
    }

    private void OnFontSizeCycle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.Columns.CycleFontSize();
        UpdateFontSizeButton();
    }

    private void OnFontSizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || DataContext is not MainViewModel vm)
            return;
        vm.Columns.CycleFontSize(true);
        UpdateFontSizeButton();
    }

    private void UpdateFontSizeButton()
    {
        if (DataContext is not MainViewModel vm)
            return;
        FontSizeButton.Content = $"{vm.Columns.FontSize}pt";
    }

    private void OnColumnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock { Tag: string column })
            return;
        if (DataContext is not MainViewModel vm)
            return;
        vm.SortBy(column);
    }

    private void OnSearchDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not AutoCompleteBox { SelectedItem: string name } box ||
            !string.Equals(box.Text, name, StringComparison.OrdinalIgnoreCase))
        {
            _activateOnSearchClose = false;
            return;
        }

        ScrollToApp(name);
        _searchHistory = SearchHistoryService.AddEntry(_searchHistory, name);
        _historyIndex = -1;
        if (_activateOnSearchClose)
        {
            _activateOnSearchClose = false;
            if (DataContext is MainViewModel vm)
            {
                var node = vm.FlatRows.OfType<AppNode>()
                             .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
                if (node != null)
                    _ = ActivateNode(node);
            }
        }

        Dispatcher.UIThread.Post(() => box.Text = string.Empty);
    }

    private void OnSearchPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Control) && SearchBox.IsDropDownOpen:
                _activateOnSearchClose = true;
                return;
            case Key.Up when !SearchBox.IsDropDownOpen && _searchHistory.Count > 0:
                _historyIndex = Math.Min(_historyIndex + 1, _searchHistory.Count - 1);
                SearchBox.Text = _searchHistory[_historyIndex];
                e.Handled = true;
                return;
            case Key.Down when !SearchBox.IsDropDownOpen && _historyIndex >= 0:
                _historyIndex--;
                SearchBox.Text = _historyIndex >= 0 ? _searchHistory[_historyIndex] : string.Empty;
                e.Handled = true;
                return;
        }

        if (e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.Enter)
            _historyIndex = -1;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (sender is not AutoCompleteBox box || box.IsDropDownOpen)
            return;
        var text = box.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;
        ScrollToApp(text);
        _searchHistory = SearchHistoryService.AddEntry(_searchHistory, text);
        _historyIndex = -1;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainViewModel vm)
        {
            var node = vm.AllNodes
                         .FirstOrDefault(n => string.Equals(n.Name, text, StringComparison.OrdinalIgnoreCase));
            if (node != null)
                _ = ActivateNode(node);
        }

        Dispatcher.UIThread.Post(() => box.Text = string.Empty);
    }

    private void ScrollToApp(string name)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (vm.EnsureAppVisible(name))
        {
            _pendingScrollApp = name;
            return;
        }

        AppNode? found = null;
        for (var i = 0; i < vm.FlatRows.Count; i++)
        {
            if (vm.FlatRows[i] is not AppNode node ||
                !node.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            ScrollToFlatRowIndex(i, GetStickyOffset(vm));
            found = node;
            break;
        }

        if (found != null)
            FlashNode(found);
    }

    private static void FlashNode(AppNode node)
    {
        node.IsSearchFx = true;
        Task.Delay(1600).ContinueWith(_ =>
                                          Dispatcher.UIThread.Post(() => node.IsSearchFx = false));
    }

    private void OnColumnResize(object? sender, VectorEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        if (sender is not Thumb thumb)
            return;

        var delta = e.Vector.X;
        switch (thumb.Tag as string)
        {
            case "Name": vm.Columns.Name += delta; break;
            case "Version": vm.Columns.Version -= delta; break;
            case "Download": vm.Columns.Download -= delta; break;
            case "Install": vm.Columns.Install -= delta; break;
            case "Joined": vm.Columns.Joined -= delta; break;
            case "Updated": vm.Columns.Updated -= delta; break;
            case "Used": vm.Columns.Used -= delta; break;
        }
    }

    private void SaveSettings()
    {
        if (DataContext is not MainViewModel vm)
            return;
        var theme = Application.Current?.RequestedThemeVariant;
        var settings = SettingsService.Load();
        settings.ViewPresets.Remove("Default");
        settings.SortColumn = vm.Columns.SortColumn;
        settings.SortDescending = vm.Columns.SortDescending;
        settings.ColumnName = vm.Columns.Name;
        settings.ColumnVersion = vm.Columns.Version;
        settings.ColumnDownload = vm.Columns.Download;
        settings.ColumnInstall = vm.Columns.Install;
        settings.ColumnJoined = vm.Columns.Joined;
        settings.ColumnUpdated = vm.Columns.Updated;
        settings.ColumnUsed = vm.Columns.Used;
        settings.Theme = theme == ThemeVariant.Light ? "Light" : theme == ThemeVariant.Dark ? "Dark" : "Default";
        SettingsService.Save(settings);
        SearchHistoryService.Save(_searchHistory);
    }

    private async void OnSaveView(object? sender, RoutedEventArgs e)
    {
        try
        {
            await OnSaveViewAsync();
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.SaveViewFailedFormat, ex.Message));
        }
    }

    private async Task OnSaveViewAsync()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var dialog = new SaveViewDialog(vm.InstallFilter);
        await dialog.ShowDialog(this);

        if (dialog.SelectedFilters.Count == 0)
            return;

        var settings = SettingsService.Load();

        if (dialog.DialogAction == SaveViewDialog.Action.Reset)
        {
            foreach (var filter in dialog.SelectedFilters)
                settings.ViewPresets.Remove(filter.ToString());
        }
        else
        {
            var preset = new FilterViewSettings
            {
                CategoryDisplay = vm.CategoryDisplay,
                CategoryScope = vm.CategoryScope,
                FontSize = vm.Columns.FontSize,
                IconSize = vm.Columns.IconSize,
                IsGridView = vm.Columns.IsGridView,
                WindowWidth = Width,
                WindowHeight = Height
            };
            foreach (var filter in dialog.SelectedFilters)
                settings.ViewPresets[filter.ToString()] = preset;
        }

        SettingsService.Save(settings);

        if (dialog.SelectedFilters.Contains(vm.InstallFilter))
            ApplyViewPreset(vm);
    }

    private void ApplyViewPreset(MainViewModel vm, bool centerWindow = true)
    {
        var settings = SettingsService.Load();
        settings.ViewPresets.TryGetValue(vm.InstallFilter.ToString(), out var preset);
        preset ??= _defaultView;

        vm.BeginPresetUpdate();
        try
        {
            vm.CategoryDisplay = preset.CategoryDisplay;
            vm.Columns.FontSize = preset.FontSize;
            vm.Columns.IconSize = preset.IconSize;
            vm.Columns.IsGridView = preset.IsGridView;
            vm.CategoryScope = preset.CategoryScope;
        }
        finally
        {
            vm.EndPresetUpdate();
        }

        ColumnHeaderBar.IsVisible = !preset.IsGridView;
        UpdateIconSizeButton();
        UpdateViewModeButton();
        UpdateFontSizeButton();
        UpdateCategoryScopeButton();
        UpdateCategoryDisplayButton();
        (IconSizeButton.Parent as Control)?.InvalidateMeasure();
        _ = ReloadIconsForSizeAsync(vm);

        if (_suppressWindowTransition)
            return;

        StartWindowTransitionToPreset(preset, centerWindow);
    }

    private Task StartWindowTransitionToPreset(FilterViewSettings preset, bool centerWindow)
    {
        var targetWidth = preset.WindowWidth;
        var targetHeight = preset.WindowHeight;

        if (!centerWindow)
        {
            Width = targetWidth;
            Height = targetHeight;
            return Task.CompletedTask;
        }

        var deltaX = (int)((targetWidth - Width) / 2);
        var deltaY = (int)((targetHeight - Height) / 2);

        PixelPoint? targetPosition = null;
        var screen = Screens.ScreenFromWindow(this);
        if (screen != null)
        {
            var wa = screen.WorkingArea;
            var scale = RenderScaling;
            var physW = (int)(targetWidth * scale);
            var physH = (int)(targetHeight * scale);
            var posX = Position.X - deltaX;
            var posY = Position.Y - deltaY;
            var x = Math.Clamp(posX, wa.X, wa.X + wa.Width - physW);
            var y = Math.Clamp(posY, wa.Y, wa.Y + wa.Height - physH);
            targetPosition = new PixelPoint(x, y);
        }

        return AnimateWindowTransitionAsync(targetWidth, targetHeight, targetPosition);
    }

    private Task AnimateWindowTransitionAsync(double targetWidth, double targetHeight, PixelPoint? targetPosition)
    {
        const int durationMs = 500;
        var startWidth = Width;
        var startHeight = Height;
        var startPos = Position;
        var endPos = targetPosition ?? Position;
        var sw = Stopwatch.StartNew();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
            var eased = 1 - Math.Pow(1 - t, 3);

            Width = startWidth + (targetWidth - startWidth) * eased;
            Height = startHeight + (targetHeight - startHeight) * eased;
            if (targetPosition != null)
            {
                var x = (int)(startPos.X + (endPos.X - startPos.X) * eased);
                var y = (int)(startPos.Y + (endPos.Y - startPos.Y) * eased);
                Position = new PixelPoint(x, y);
            }

            if (t < 1.0)
                return;
            timer.Stop();
            tcs.TrySetResult();
        };
        timer.Start();
        return tcs.Task;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            if (_forceClose)
                return;

            if (_installQueue.IsRunning)
            {
                e.Cancel = true;
                var appName = _installQueue.ActiveNode?.Name ?? UiText.Dialog.MainCloseInProgressCurrentApp;
                var confirmDialog = new AppDialog(
                    UiText.Dialog.MainCloseInProgressTitle,
                    string.Format(UiText.Dialog.MainCloseInProgressActiveFormat, appName) +
                    UiText.Dialog.MainCloseInProgressBody,
                    UiText.Button.CloseAnyway, UiText.Button.KeepRunning);
                if (_installQueue.ActiveNode != null)
                    confirmDialog.Icon = new WindowIcon(_installQueue.ActiveNode.Icon);
                await confirmDialog.ShowDialog(this);
                if (confirmDialog.Result != UiText.Button.CloseAnyway)
                    return;
                await _installQueue.Cts?.CancelAsync()!;
                AppDeployService.KillActiveInstaller();
                if (_installQueue.ActiveDownloadFile is { } activeFile)
                {
                    var partialFile = Path.Combine(AppDeployService.AppsDir, activeFile);
                    try
                    {
                        File.Delete(partialFile);
                    }
                    catch
                    {
                        /* file may not exist yet or still locked by the installer */
                    }
                }

                _installQueue.ClearQueue();
            }

            SaveSettings();
            _forceClose = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Write(string.Format(LogText.Main.CloseConfirmationFailedFormat, ex.Message));
            _forceClose = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        _ipcServer?.Dispose();
        _ipcDebounceCts?.Cancel();
        _ipcDebounceCts?.Dispose();
        _installQueue.Dispose();
        _iconManager.Dispose();
        _deployService.Dispose();
        base.OnClosed(e);
    }
}
