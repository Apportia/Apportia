using System.Diagnostics;
using Apportia.Models;
using Apportia.Platform;
using Apportia.Services;
using Apportia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private int _historyIndex = -1;
    private CancellationTokenSource? _iconDownloadCts;
    private CancellationTokenSource? _ipcDebounceCts;
    private IpcServer? _ipcServer;
    private string? _pendingScrollApp;
    private string? _pendingScrollTarget;
    private bool _pendingScrollTop;
    private double? _pendingScrollY;
    private List<string> _searchHistory = [];

    private SelfUpdateCoordinator _selfUpdate = null!;

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

        _ = StartupAsync();
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    // Icons with an OS-specific tint variant live under Assets/Emoji/win/.
    public static string OpenFolderIconPath => IsWindows
        ? "avares://Apportia/Assets/Emoji/win/1f4c1.svg"
        : "avares://Apportia/Assets/Emoji/1f4c1.svg";

    private ItemsControl ActiveList =>
        (DataContext as MainViewModel)?.Columns.IsGridView == true ? MainGridList : MainList;

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

    private async Task StartupAsync()
    {
        // Yield so the window renders before we touch the JSON or filesystem.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        if (File.Exists(AppDatabaseUpdater.CachePath))
        {
            await PopulateFromCacheAsync();
            if (await ResolveUnknownAppDirsAsync())
                await PopulateFromCacheAsync();
            _ = Task.WhenAll(
                AppDatabaseUpdater.TryUpdateAsync(_cts.Token),
                MirrorService.TryUpdateAsync(_cts.Token),
                SecurityNoticeService.TryUpdateAsync(_cts.Token));
            return;
        }

        await StartFirstRunAsync();
    }

    private async Task PopulateFromCacheAsync()
    {
        var vm = await Task.Run(BuildViewModel);
        SubscribeViewModel(vm);
        DataContext = vm;
        ApplyViewPreset(vm, false);
    }

    private MainViewModel BuildViewModel()
    {
        var settings = SettingsService.Load();
        _defaultView = FilterViewSettings.Default;

        var vm = new MainViewModel(AppDatabaseParser.ParseJson(AppDatabaseUpdater.CachePath), _iconManager, _defaultView.IconSize)
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
        vm.InstallFilter = settings.InstallFilter;

        return vm;
    }

    private void ApplyPersistedShell()
    {
        var settings = SettingsService.Load();
        settings.ViewPresets.TryGetValue(settings.InstallFilter.ToString(), out var preset);
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
            await PopulateFromCacheAsync();
            if (await ResolveUnknownAppDirsAsync())
                await PopulateFromCacheAsync();
        });
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
                "Unknown App Folder",
                $"Found an app folder that isn't registered in the app database:\n\n{name}\n\nWhat should Apportia do with it?",
                "Move to CustomApps", "Delete", "Skip");
            await choose.ShowDialog(this);

            if (choose.Result == "Move to CustomApps")
            {
                if (await ImportUnknownAsCustomAsync(dir, vm))
                    changed = true;
            }
            else if (choose.Result == "Delete")
            {
                var confirm = new AppDialog(
                    "Delete Folder",
                    $"Permanently delete \"{name}\" and everything inside it?",
                    "Delete", "Cancel");
                await confirm.ShowDialog(this);
                if (confirm.Result == "Delete")
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
                move: true);
        }
        catch
        {
            return false;
        }

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
                false when string.Equals(n.Category, "Games", StringComparison.OrdinalIgnoreCase) && hideGames => false,
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
            var container = ActiveList.ContainerFromIndex(i);
            var pos = container?.TranslatePoint(new Point(0, 0), MainList);
            if (pos.HasValue)
                MainScroller.Offset = new Vector(0, pos.Value.Y - GetStickyOffset(vm));
            return;
        }
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
        catch
        {
            /* activation failed – UI remains in its current state */
        }
    }

    private async Task ActivateNode(AppNode node, bool ctrlHeld = false)
    {
        if (node.IsCustom)
        {
            await TryLaunchWithArgsAsync(node);
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
                    $"{node.Name} is currently in the installation queue.\n\nWould you like to remove it?",
                    "Remove from Queue", "Cancel");
                if (remove != "Remove from Queue")
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
                        $"{node.Name} is currently being downloaded.\n\nWould you like to cancel the installation?",
                        "Cancel Installation", "Keep Running");
                    if (cancel != "Cancel Installation")
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

                var action = File.Exists(marker) ? "update" : "install";
                var queue = await ShowDialog(
                    node, node.Name,
                    $"An installation is already in progress.\n\nAdd {node.Name} to the queue to {action} it afterward?",
                    "Add to Queue", "Cancel");
                if (queue != "Add to Queue")
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
                    node, $"Update Available \u2014 {node.Name}",
                    $"A newer version of {node.Name} is available.\n\nWould you like to update now?",
                    "Update", "Cancel");
                if (choice == "Update")
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
                    $"Would you like to install {node.Name}?",
                    "Install", "Cancel");
                if (choice == "Install")
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
                $"{node.Name} is currently in the installation queue.\n\nWould you like to remove it?",
                "Remove from Queue", "Cancel");
            if (remove != "Remove from Queue")
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
                    $"{node.Name} is currently being downloaded.\n\nWould you like to cancel the installation?",
                    "Cancel Installation", "Keep Running");
                if (cancel != "Cancel Installation")
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

            var action = appExe != null ? "update" : "install";
            var queue = await ShowDialog(
                node, node.Name,
                $"An installation is already in progress.\n\nAdd {node.Name} to the queue to {action} it afterward?",
                "Add to Queue", "Cancel");
            if (queue != "Add to Queue")
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
                node, $"Update Available \u2014 {node.Name}",
                $"A newer version of {node.Name} is available.\n\nWould you like to update now?",
                "Update & Run", "Update", "Run", "Cancel");

            switch (choice)
            {
                case "Update & Run":
                    await _installer.InstallAsync(node, appsBaseDir, true);
                    break;
                case "Update":
                    await _installer.InstallAsync(node, appsBaseDir, false);
                    break;
                case "Run":
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
            $"Would you like to install {node.Name}?",
            "Install", "Install & Run", "Cancel");

        switch (installChoice)
        {
            case "Install":
                await _installer.InstallAsync(node, appsBaseDir, false);
                break;
            case "Install & Run":
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
        if (NodeFromMenu(sender) is { } node)
            _ = _installer.InstallAsync(node, AppDeployService.AppsDir, true);
    }

    private void OnMenuUpdate(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            _ = _installer.InstallAsync(node, AppDeployService.AppsDir, false);
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
        catch
        {
            /* cancellation dialog failed – the install continues uninterrupted */
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
                    "Installation Running",
                    $"{_installQueue.ActiveNode.Name} is currently being installed.\n\n" +
                    "Canceling now may leave the application in a corrupt state.\n\n" +
                    "Are you sure you want to cancel?",
                    "Cancel Installation", "Keep Running")
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

            if (dialog.Result != "Cancel Installation")
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
        catch
        {
            /* launch failed – the app was not started, UI stays unchanged */
        }
    }

    private async void OnMenuTerminate(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (NodeFromMenu(sender) is not { } node)
                return;
            var candidates = RunningAppsService.GetKillCandidates(node.SectionName);
            if (candidates.Count == 0)
                return;

            var dialog = new TerminateDialog(node.Name, candidates)
            {
                Icon = new WindowIcon(node.Icon)
            };
            await dialog.ShowDialog(this);
            if (!dialog.Confirmed)
                return;

            RunningAppsService.KillPids(candidates.Select(c => c.Pid));
        }
        catch
        {
            /* terminate confirmation failed – no processes were killed */
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
        catch
        {
            /* args dialog or launch failed – the app was not started */
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
                ? $"Remove {node.Name} and all its data?\n\n" +
                  $"This is the last app requiring Java.\n\nThe following Java plugins will also be uninstalled:\n" +
                  string.Join("\n", javaPluginsToRemove.Select(n => $"\u2022 {n.Name}"))
                : $"Remove {node.Name} and all its data?";

            var confirmed = await ShowDialog(node, "Uninstall", message, "Uninstall", "Cancel");
            if (confirmed != "Uninstall")
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
                    node, "App is Running",
                    $"{node.Name} has running processes:\n\n{names}\n\nForce-quit them to proceed?",
                    "Force Quit & Uninstall", "Cancel");
                if (forceQuit != "Force Quit & Uninstall")
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
                        node, "Backup User Data",
                        $"Do you want to save a backup of your {node.Name} data before uninstalling?",
                        "Save Backup", "Skip");

                    if (doBackup == "Save Backup")
                    {
                        if (AppBackupService.HasBackup(node.SectionName))
                        {
                            var choice = await ShowDialog(
                                node, "Backup Already Exists",
                                $"A backup of {node.Name}'s data already exists.\n\nWhich backup do you want to keep?",
                                "Keep New", "Keep Existing");

                            if (choice == "Keep New")
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

                node.IsInstalled = false;

                foreach (var javaNode in javaPluginsToRemove)
                {
                    var javaDir = PluginService.GetInstallDir(javaNode.SectionName);
                    if (Directory.Exists(javaDir))
                        Directory.Delete(javaDir, true);
                    javaNode.IsInstalled = false;
                }

                if (javaPluginsToRemove.Count <= 0)
                    return;

                var commonFilesDir = PluginService.GetInstallDir();
                if (Directory.Exists(commonFilesDir) && !Directory.EnumerateFileSystemEntries(commonFilesDir).Any())
                    Directory.Delete(commonFilesDir);
            }
            catch (Exception ex)
            {
                await ShowDialog(node, "Uninstall Failed", ex.Message, "OK");
            }
        }
        catch
        {
            /* confirmation dialog failed – the uninstall was not performed */
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
            if (!node.IsPlugin)
            {
                AppExecutableService.Remove(node.SectionName);
                LocalVersionService.Remove(node.SectionName);
                node.LocalDisplayVersion = null;
                node.LocalPackageVersion = null;
            }

            node.IsInstalled = false;
        }
        catch (Exception ex)
        {
            await ShowDialog(node, "Uninstall Failed", ex.Message, "OK");
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
                var iconChanged = !string.IsNullOrEmpty(win.IconSourcePath);
                await CustomAppService.UpdateAppAsync(
                    node.SectionName,
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
                    node.SectionName,
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
                    ? _iconManager.ReloadCustomIcon(node.SectionName)
                    : _iconManager.GetCustomIcon(node.SectionName, vm.Columns.IconSize);

                var oldUsedBytes = node.UsedBytes;
                vm.RemoveCustomApp(node);
                var newNode = vm.AddCustomApp(entry, icon);
                newNode.SetUsedBytes(oldUsedBytes);
            }
            catch (Exception ex)
            {
                await ShowDialog(node, "Update Failed", ex.Message, "OK");
            }
        }
        catch
        {
            /* settings dialog failed – the custom app entry was not modified */
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
                var msg = new AppDialog("No Preview", $"No preview available for {node.Name}.", "OK");
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
            AppDeployService.LaunchApp(appExe, args, true);
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
        if (settings.LinuxSetupCompleted && WineService.IsWineReady())
            return true;
        try
        {
            var dialog = new LinuxSetupDialog { Icon = Icon };
            await dialog.ShowDialog(this);
        }
        catch
        {
            /* window may be closing */
        }

        return WineService.IsWineReady();
    }

    private async void OnImportApp(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm)
                return;
            var win = new CustomAppWindow(vm.Categories, vm.SubCategoriesMap);
            await win.ShowDialog(this);
            if (!win.Success)
                return;

            try
            {
                while (true)
                {
                    var sourceSize = await Task.Run(() => AppDiskUsageService.GetDirectorySize(win.FolderName));
                    var required = (long)(sourceSize * 1.1);
                    var available = AppDiskUsageService.GetAvailableFreeSpace(CustomAppService.CustomAppsDir);
                    if (available < required)
                    {
                        var choice = await ShowDiskSpaceDialog(null, win.Name, required, available);
                        if (choice == "Retry")
                            continue;
                        return;
                    }

                    break;
                }

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
                    copyDialog.CancellationToken);
                _ = importTask.ContinueWith(t =>
                                                Dispatcher.UIThread.Post(t.IsCompletedSuccessfully
                                                                             ? copyDialog.NotifyDone
                                                                             : copyDialog.Close));
                await copyDialog.ShowDialog(this);
                var folderName = await importTask;

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
                await ShowDialog("Add App Failed", ex.Message, "OK");
            }
        }
        catch
        {
            /* import dialog failed – no custom app was added to the list */
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
        var msg = $"Not enough disk space to install {appName}.\n\n" +
                  $"Required:   {AppDiskUsageService.FormatSize(required)}\n" +
                  $"Available:  {AppDiskUsageService.FormatSize(available)}\n\n" +
                  "Free up disk space and click Retry, or Cancel to abort.";
        return node != null
            ? ShowDialog(node, $"{appName} \u2014 Not Enough Space", msg, "Retry", "Cancel")
            : ShowDialog($"{appName} \u2014 Not Enough Space", msg, "Retry", "Cancel");
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
            Title = $"Security Notice \u2014 {node.Name}",
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
        };
        Closing += OnWindowClosing;
        MainScroller.ScrollChanged += (_, _) => UpdateStickyHeader();
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
                : string.Join("\n", args.Take(maxDisplay)) + $"\n... and {args.Length - maxDisplay} more";
            await ShowDialog("Arguments Updated",
                             $"A second instance was launched with new CLI arguments:\n\n{preview}\n\nThese have replaced the current arguments.",
                             "OK");
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
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(600);
            await new TipsDialog { Icon = Icon }.ShowDialog(this);
        });
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
                    break;
            }
        };
        SearchBox.AsyncPopulator = (text, _) =>
            Task.FromResult(vm.SearchAppNames(text ?? string.Empty).Cast<object>());
        vm.BeforeRebuildRows += () =>
        {
            _pendingScrollY = MainScroller.Offset.Y;
            _pendingScrollTarget ??= FindTopVisibleSectionName();
        };
        vm.RowsFullyLoaded += () =>
        {
            if (_pendingScrollTop)
            {
                _pendingScrollTop = false;
                _pendingScrollY = null;
                _pendingScrollTarget = null;
                MainScroller.Offset = new Vector(0, 0);
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
                        MainScroller.Offset = new Vector(0, y);
                }, DispatcherPriority.Background);
            }
            else if (_pendingScrollY is { } y)
            {
                _pendingScrollY = null;
                Dispatcher.UIThread.Post(() => MainScroller.Offset = new Vector(0, y), DispatcherPriority.Background);
            }

            UpdateStickyHeader();
        };
    }

    private string? FindTopVisibleSectionName()
    {
        if (DataContext is not MainViewModel vm)
            return null;
        var scrollY = MainScroller.Offset.Y;
        for (var i = 0; i < vm.FlatRows.Count; i++)
        {
            if (vm.FlatRows[i] is not AppNode node)
                continue;
            var container = ActiveList.ContainerFromIndex(i);
            var pos = container?.TranslatePoint(new Point(0, 0), MainList);
            if (pos is { Y: var y } && y >= scrollY)
                return node.SectionName;
        }

        return null;
    }

    private bool TryScrollToSectionName(string sectionName)
    {
        if (DataContext is not MainViewModel vm)
            return false;
        for (var i = 0; i < vm.FlatRows.Count; i++)
        {
            if (vm.FlatRows[i] is not AppNode node || node.SectionName != sectionName)
                continue;
            var container = ActiveList.ContainerFromIndex(i);
            if (container is null)
                return false;
            var pos = container.TranslatePoint(new Point(0, 0), MainList);
            if (pos.HasValue)
                MainScroller.Offset = new Vector(0, pos.Value.Y - GetStickyOffset(vm));
            return true;
        }

        return false;
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

        var scrollY = MainScroller.Offset.Y;
        string? currentCategory = null;

        for (var i = 0; i < vm.FlatRows.Count; i++)
        {
            if (vm.FlatRows[i] is not CategoryNode { HasCategory: true } catHeader)
                continue;
            var container = ActiveList.ContainerFromIndex(i);
            if (container is null)
                continue;
            var pos = container.TranslatePoint(new Point(0, 0), MainList);
            if (pos is { Y: var y } && y < scrollY)
                currentCategory = catHeader.Category;
            else
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
            CategoryScope.Full => "Full",
            CategoryScope.Extended => "Extended",
            _ => "Standard"
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
            CategoryDisplayMode.Categories => "Categories",
            CategoryDisplayMode.None => "No Groups",
            _ => "Tree"
        };
    }

    private void OnInstallFilterCycle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.InstallFilter = vm.InstallFilter switch
        {
            InstallFilter.All => InstallFilter.Installed,
            InstallFilter.Installed => InstallFilter.NotInstalled,
            _ => InstallFilter.All
        };
        UpdateInstallFilterButton();
    }

    private void OnInstallFilterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || DataContext is not MainViewModel vm)
            return;
        vm.InstallFilter = vm.InstallFilter switch
        {
            InstallFilter.All => InstallFilter.NotInstalled,
            InstallFilter.Installed => InstallFilter.All,
            _ => InstallFilter.Installed
        };
        UpdateInstallFilterButton();
    }

    private void UpdateInstallFilterButton()
    {
        if (DataContext is not MainViewModel vm)
            return;
        InstallFilterButton.Content = vm.InstallFilter switch
        {
            InstallFilter.Installed => "Installed",
            InstallFilter.NotInstalled => "Not Installed",
            _ => "All Apps"
        };
    }

    private async Task CheckForUpdateAsync()
    {
        var info = await _selfUpdate.CheckAsync();
        if (info == null)
            return;
        UpdateButton.IsVisible = true;
        UpdateButton.Content = $"Update {info.Version}";
    }

    private async void OnUpdateApp(object? sender, RoutedEventArgs e)
    {
        try
        {
            var info = _selfUpdate.Pending;
            if (info == null)
                return;

            var changelog = new ChangelogDialog(info.Version, info.Changelog);
            await changelog.ShowDialog(this);
            if (!changelog.Confirmed)
                return;

            UpdateButton.IsEnabled = false;
            ShowDownloadBar(true);
            DownloadProgressBar.IsIndeterminate = false;
            DownloadSizeText.Text = $"Downloading update {info.Version}...";
            DownloadSpeedText.Text = string.Empty;

            try
            {
                var progress = new Progress<int>(p =>
                {
                    DownloadProgressBar.Value = p;
                    DownloadSizeText.Text = $"Downloading update {info.Version}... {p}%";
                });
                await _selfUpdate.ApplyAsync(progress);
            }
            catch (Exception ex)
            {
                ShowDownloadBar(false);
                UpdateButton.IsEnabled = true;
                Log.Write($"Self-update failed: {ex.Message}");
            }
        }
        catch
        {
            /* self-update setup failed – the running version is unchanged */
        }
    }

    private void OnViewModeToggle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.Columns.IsGridView = !vm.Columns.IsGridView;
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
        ViewModeButton.Content = vm.Columns.IsGridView ? "Grid" : "List";
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

            var container = ActiveList.ContainerFromIndex(i);
            if (container is null)
                return;

            var pos = container.TranslatePoint(new Point(0, 0), MainList);
            if (pos.HasValue)
                MainScroller.Offset = new Vector(0, pos.Value.Y - GetStickyOffset(vm));
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
            case "Current": vm.Columns.Current -= delta; break;
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
        settings.InstallFilter = vm.InstallFilter;
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
        catch
        {
            /* save-view must never crash the app */
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

        vm.CategoryDisplay = preset.CategoryDisplay;
        vm.Columns.FontSize = preset.FontSize;
        vm.Columns.IconSize = preset.IconSize;
        vm.Columns.IsGridView = preset.IsGridView;
        vm.CategoryScope = preset.CategoryScope;

        var deltaX = (int)((preset.WindowWidth - Width) / 2);
        var deltaY = (int)((preset.WindowHeight - Height) / 2);
        Width = preset.WindowWidth;
        Height = preset.WindowHeight;

        UpdateIconSizeButton();
        UpdateViewModeButton();
        UpdateFontSizeButton();
        UpdateCategoryScopeButton();
        UpdateCategoryDisplayButton();
        (IconSizeButton.Parent as Control)?.InvalidateMeasure();
        _ = ReloadIconsForSizeAsync(vm);

        if (!centerWindow)
            return;

        var posX = Position.X - deltaX;
        var posY = Position.Y - deltaY;

        var screen = Screens.ScreenFromWindow(this);
        if (screen == null)
            return;

        var wa = screen.WorkingArea;
        var scale = RenderScaling;
        var physW = (int)(Width * scale);
        var physH = (int)(Height * scale);
        var x = Math.Clamp(posX, wa.X, wa.X + wa.Width - physW);
        var y = Math.Clamp(posY, wa.Y, wa.Y + wa.Height - physH);
        var target = new PixelPoint(x, y);
        Dispatcher.UIThread.Post(() => Position = target, DispatcherPriority.Background);
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
                var appName = _installQueue.ActiveNode?.Name ?? "the current app";
                var confirmDialog = new AppDialog(
                    "Installation in Progress",
                    $"{appName} is currently being installed.\n\n" +
                    "Closing now will abort the installation and may leave it in a corrupt state.\n\n" +
                    "Are you sure you want to close?",
                    "Close Anyway", "Keep Running");
                if (_installQueue.ActiveNode != null)
                    confirmDialog.Icon = new WindowIcon(_installQueue.ActiveNode.Icon);
                await confirmDialog.ShowDialog(this);
                if (confirmDialog.Result != "Close Anyway")
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
        catch
        {
            /* close confirmation failed – force the window shut to avoid being stuck open */
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