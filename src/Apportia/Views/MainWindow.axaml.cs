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

public partial class MainWindow : Window
{
    private readonly AppDatabaseUpdater _appDatabaseUpdater;

    private readonly string[] _cliAppArgs;
    private readonly CancellationTokenSource _cts = new();
    private readonly AppDownloadService _downloadService;
    private readonly IconManager _iconManager;
    private readonly Queue<(AppNode Node, bool Launch)> _installQueue = new();

    private bool _activateOnSearchClose;
    private string? _activeDownloadFile;
    private AppNode? _activeNode;

    private bool _ctrlHeld;
    private FilterViewSettings _defaultView = new();

    private bool _downloading;
    private bool _forceClose;
    private int _historyIndex = -1;
    private bool _inSetupPhase;
    private CancellationTokenSource? _installCts;
    private string? _pendingScrollApp;
    private string? _pendingScrollTarget;
    private bool _pendingScrollTop;
    private double? _pendingScrollY;

    private AppUpdateInfo? _pendingUpdate;
    private ThemeVariant? _prevTheme;
    private List<string> _searchHistory = [];
    private bool _systemIsDark;

    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var cliArgs = Environment.GetCommandLineArgs();
        _cliAppArgs = cliArgs.Length > 1 ? Environment.GetCommandLineArgs().Skip(1).ToArray() : [];

        SettingsService.ClearLog();

        var iconCacheDir = Path.Combine(AppContext.BaseDirectory, "Data", "AppImages");

        _iconManager = new IconManager(iconCacheDir);
        _appDatabaseUpdater = new AppDatabaseUpdater();
        _downloadService = new AppDownloadService(AppDownloadService.AppsDir);

        if (File.Exists(AppDatabaseUpdater.CachePath))
        {
            // Cache available: populate UI immediately, update + download icons in background
            var vm = BuildViewModel();
            SubscribeViewModel(vm);
            DataContext = vm;
            ApplyViewPreset(vm, false);
            _ = Task.WhenAll(
                _appDatabaseUpdater.TryUpdateAsync(_cts.Token),
                MirrorService.TryUpdateAsync(_cts.Token));
            return;
        }

        // No cache: download first, then populate UI
        _ = StartFirstRunAsync();
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    private ItemsControl ActiveList =>
        (DataContext as MainViewModel)?.Columns.IsGridView == true ? MainGridList : MainList;

    private MainViewModel BuildViewModel()
    {
        var settings = SettingsService.Load();
        _defaultView = FilterViewSettings.Default;

        Width = _defaultView.WindowWidth;
        Height = _defaultView.WindowHeight;

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

        Application.Current!.RequestedThemeVariant = settings.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => null
        };

        return vm;
    }

    private async Task StartFirstRunAsync()
    {
        await Task.WhenAll(
            _appDatabaseUpdater.TryUpdateAsync(_cts.Token),
            MirrorService.TryUpdateAsync(_cts.Token));
        var vm = BuildViewModel();
        SubscribeViewModel(vm);
        await Dispatcher.UIThread.InvokeAsync(() => DataContext = vm);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyViewPreset(vm, false));
    }

    private async Task StartIconDownloadsAsync(MainViewModel vm)
    {
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

        var nodeMap = nodes.ToDictionary(n => n.SectionName, n => n);

        await _iconManager.DownloadAllAsync(
            nodeMap.Keys,
            vm.Columns.IconSize,
            (section, bitmap) =>
            {
                if (!nodeMap.TryGetValue(section, out var node))
                    return;
                Dispatcher.UIThread.Post(() => node.Icon = bitmap);
            },
            _cts.Token);
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
        var node = vm.FlatRows
                     .OfType<CategoryNode>()
                     .FirstOrDefault(n => n.Category == name);
        node?.IsExpanded = !node.IsExpanded;
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

        var appsBaseDir = AppDownloadService.AppsDir;

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
                RemoveFromQueue(node);
                if (_downloading && node == _activeNode && _installCts != null)
                    await _installCts.CancelAsync();
                else if (node.IsInstalled)
                    await SilentUninstallAsync(node, appsBaseDir);
                return;
            }

            if (_downloading)
            {
                if (node == _activeNode)
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
                    node.IsQueued = true;
                    _installQueue.Enqueue((node, false));
                    if (_downloading || !_installQueue.TryDequeue(out var nextCtrl))
                        return;
                    nextCtrl.Node.IsQueued = false;
                    _ = InstallApp(nextCtrl.Node, appsBaseDir, nextCtrl.Launch);
                    return;
                }

                var action = File.Exists(marker) ? "update" : "install";
                var queue = await ShowDialog(
                    node, node.Name,
                    $"An installation is already in progress.\n\nAdd {node.Name} to the queue to {action} it afterward?",
                    "Add to Queue", "Cancel");
                if (queue != "Add to Queue")
                    return;
                node.IsQueued = true;
                _installQueue.Enqueue((node, false));
                if (_downloading || !_installQueue.TryDequeue(out var next))
                    return;
                next.Node.IsQueued = false;
                _ = InstallApp(next.Node, appsBaseDir, next.Launch);
                return;
            }

            if (File.Exists(marker))
            {
                if (ctrlHeld)
                {
                    await InstallApp(node, appsBaseDir, false);
                    return;
                }

                var choice = await ShowDialog(
                    node, $"Update Available \u2014 {node.Name}",
                    $"A newer version of {node.Name} is available.\n\nWould you like to update now?",
                    "Update", "Cancel");
                if (choice == "Update")
                    await InstallApp(node, appsBaseDir, false);
            }
            else
            {
                if (ctrlHeld)
                {
                    await InstallApp(node, appsBaseDir, false);
                    return;
                }

                var choice = await ShowDialog(
                    node, node.Name,
                    $"Would you like to install {node.Name}?",
                    "Install", "Cancel");
                if (choice == "Install")
                    await InstallApp(node, appsBaseDir, false);
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

            RemoveFromQueue(node);
            if (_downloading && node == _activeNode && _installCts != null)
                await _installCts.CancelAsync();
            else if (node.IsInstalled)
                await SilentUninstallAsync(node, appsBaseDir);
            return;
        }

        if (_downloading)
        {
            if (node == _activeNode)
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
                node.IsQueued = true;
                _installQueue.Enqueue((node, false));
                if (_downloading || !_installQueue.TryDequeue(out var nextCtrl))
                    return;
                nextCtrl.Node.IsQueued = false;
                _ = InstallApp(nextCtrl.Node, appsBaseDir, nextCtrl.Launch);
                return;
            }

            var action = appExe != null ? "update" : "install";
            var queue = await ShowDialog(
                node, node.Name,
                $"An installation is already in progress.\n\nAdd {node.Name} to the queue to {action} it afterward?",
                "Add to Queue", "Cancel");
            if (queue != "Add to Queue")
                return;
            node.IsQueued = true;
            _installQueue.Enqueue((node, false));
            if (_downloading || !_installQueue.TryDequeue(out var next))
                return;
            next.Node.IsQueued = false;
            _ = InstallApp(next.Node, appsBaseDir, next.Launch);
            return;
        }

        if (appExe != null)
        {
            if (ctrlHeld)
            {
                await InstallApp(node, appsBaseDir, false);
                return;
            }

            var choice = await ShowDialog(
                node, $"Update Available \u2014 {node.Name}",
                $"A newer version of {node.Name} is available.\n\nWould you like to update now?",
                "Update & Run", "Update", "Run", "Cancel");

            switch (choice)
            {
                case "Update & Run":
                    await InstallApp(node, appsBaseDir, true);
                    break;
                case "Update":
                    await InstallApp(node, appsBaseDir, false);
                    break;
                case "Run":
                    await TryLaunchWithArgsAsync(node);
                    break;
            }

            return;
        }

        if (ctrlHeld)
        {
            await InstallApp(node, appsBaseDir, false);
            return;
        }

        var installChoice = await ShowDialog(
            node, node.Name,
            $"Would you like to install {node.Name}?",
            "Install", "Install & Run", "Cancel");

        switch (installChoice)
        {
            case "Install":
                await InstallApp(node, appsBaseDir, false);
                break;
            case "Install & Run":
                await InstallApp(node, appsBaseDir, true);
                break;
        }
    }

    private void OnMenuInstallRun(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            _ = InstallApp(node, AppDownloadService.AppsDir, true);
    }

    private void OnMenuInstall(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            _ = InstallApp(node, AppDownloadService.AppsDir, false);
    }

    private void OnMenuUpdateRun(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            _ = InstallApp(node, AppDownloadService.AppsDir, true);
    }

    private void OnMenuUpdate(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            _ = InstallApp(node, AppDownloadService.AppsDir, false);
    }

    private void OnMenuAddToQueue(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is not { } node)
            return;
        node.IsQueued = true;
        _installQueue.Enqueue((node, false));
        if (_downloading || !_installQueue.TryDequeue(out var next))
            return;
        next.Node.IsQueued = false;
        _ = InstallApp(next.Node, AppDownloadService.AppsDir, next.Launch);
    }

    private void OnMenuRemoveFromQueue(object? sender, RoutedEventArgs e)
    {
        if (NodeFromMenu(sender) is { } node)
            RemoveFromQueue(node);
    }

    private void RemoveFromQueue(AppNode node)
    {
        node.IsQueued = false;
        var remaining = _installQueue.Where(i => i.Node != node).ToArray();
        _installQueue.Clear();
        foreach (var item in remaining)
            _installQueue.Enqueue(item);
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
        if (_activeNode == null || _installCts == null)
            return;

        if (_inSetupPhase)
        {
            var watchCts = new CancellationTokenSource();
            var watchToken = watchCts.Token;
            var dialog = new AppDialog(
                    "Installation Running",
                    $"{_activeNode.Name} is currently being installed.\n\n" +
                    "Canceling now may leave the application in a corrupt state.\n\n" +
                    "Are you sure you want to cancel?",
                    "Cancel Installation", "Keep Running")
                { Icon = new WindowIcon(_activeNode.Icon) };

            var watchTask = Task.Run(async () =>
            {
                while (_inSetupPhase && !watchToken.IsCancellationRequested)
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

        await _installCts.CancelAsync();
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
                var converted = AppDownloadService.ConvertArgsForWine(dialog.ArgsArray);
                args = RunArgsDialog.CombineArgs(converted);
            }

            node.TryBeginLaunchFx();
            if (dialog.Choice == RunArgsDialog.RunChoice.WithArgsAsAdmin)
                await Task.Run(() => RunAsAdmin(node, args));
            else if (node.IsCustom)
                RunCustomApp(node, args);
            else
                RunApp(node, AppDownloadService.AppsDir, args);
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
            var appsBaseDir = AppDownloadService.AppsDir;

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
                AppExecutableService.Remove(node.SectionName);
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
                    string.Empty,
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
                    : _iconManager.GetCustomIcon(node.SectionName);

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
            var appDir = AppDownloadService.GetInstallDir(node.SectionName);
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
            : AppDownloadService.GetInstallDir(node.SectionName);
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
            AppDownloadService.LaunchApp(appExe, args);
    }

    private static void RunCustomApp(AppNode node, string? args = null)
    {
        var appExe = Path.Combine(CustomAppService.CustomAppsDir, node.SectionName, node.DownloadFile);
        if (File.Exists(appExe))
            AppDownloadService.LaunchApp(appExe, args);
    }

    private async Task TryLaunchWithArgsAsync(AppNode node)
    {
        if (!node.TryBeginLaunchFx())
            return;

        if (_cliAppArgs.Length > 0)
        {
            var dialog = new RunArgsDialog(node.Name, _cliAppArgs) { Icon = new WindowIcon(node.Icon) };
            await dialog.ShowDialog(this);
            if (dialog.Choice == RunArgsDialog.RunChoice.Cancel)
                return;
            string? args = null;
            if (dialog.Choice is RunArgsDialog.RunChoice.WithArgs or RunArgsDialog.RunChoice.WithArgsAsAdmin)
            {
                var converted = AppDownloadService.ConvertArgsForWine(dialog.ArgsArray);
                args = RunArgsDialog.CombineArgs(converted);
            }

            if (dialog.Choice == RunArgsDialog.RunChoice.WithArgsAsAdmin)
                await Task.Run(() => RunAsAdmin(node, args));
            else if (node.IsCustom)
                RunCustomApp(node, args);
            else
                RunApp(node, AppDownloadService.AppsDir, args);
        }
        else
        {
            if (node.IsCustom)
                RunCustomApp(node);
            else
                RunApp(node, AppDownloadService.AppsDir);
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

                var folderName = await CustomAppService.ImportAppAsync(
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
                    win.DisplayVersion);

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
                    string.Empty,
                    win.Version,
                    win.Version,
                    win.UpdateDate,
                    win.ExeFile,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);

                var icon = _iconManager.GetCustomIcon(folderName);
                var newNode = vm.AddCustomApp(entry, icon);
                var appDir = Path.Combine(CustomAppService.CustomAppsDir, folderName);
                _ = ScanAndCacheNodeSizeAsync(newNode, appDir);
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

    private async Task InstallApp(AppNode node, string appsBaseDir, bool launch, bool fromQueue = false)
    {
        if (_downloading && !fromQueue)
            return;
        if (string.IsNullOrEmpty(node.DownloadFile) || string.IsNullOrEmpty(node.DownloadPath))
            return;

        if (OperatingSystem.IsLinux() && !AppDownloadService.IsWineAvailable())
        {
            await ShowDialog(
                node, "Wine Not Found",
                "Running Windows applications requires Wine.\n\n" +
                "Please install Wine using your package manager.",
                "OK");
            return;
        }

        // Language selection – resolve before locking the download state
        var downloadFile = node.DownloadFile;
        var downloadHash = node.Hash;
        string? chosenLanguage = null;

        if (node.HasLanguageVariants)
        {
            var savedLang = AppLanguageService.Load(node.SectionName);

            var autoSelected = savedLang == "English" ||
                               savedLang != null && node.HasLanguageVariantKey(savedLang);

            if (!autoSelected)
            {
                var dialog = new LanguageDialog(node.Name, node.GetLanguageKeys()!, savedLang) { Icon = new WindowIcon(node.Icon) };
                await dialog.ShowDialog(this);
                if (dialog.SelectedLanguageKey is null)
                    return;
                chosenLanguage = dialog.SelectedLanguageKey;
            }
            else
            {
                chosenLanguage = savedLang;
            }

            if (chosenLanguage != "English" && node.TryGetLanguageVariant(chosenLanguage!, out var variantFile, out var variantHash))
            {
                downloadFile = variantFile;
                downloadHash = variantHash;
            }
        }

        // Java requirement check – runs before locking the download state
        if (node.RequiresJava && DataContext is MainViewModel vmJava)
        {
            var javaInstalled = vmJava.AllNodes
                                      .Any(n => PluginService.IsJavaPlugin(n.SectionName) && n.IsInstalled);

            if (!javaInstalled)
            {
                var available = vmJava.AllNodes
                                      .Where(n => PluginService.IsJavaPlugin(n.SectionName) && n is { IsInstalled: false, IsLegacy: false })
                                      .ToList();

                if (available.Count == 0)
                {
                    await ShowDialog(
                        node, "Java Required",
                        $"{node.Name} requires a Java runtime, but no Java plugins are available to install.",
                        "OK");
                    return;
                }

                var javaDialog = new JavaRequiredDialog(node.Name, available.Select(n => n.Name).ToArray()) { Icon = new WindowIcon(node.Icon) };
                await javaDialog.ShowDialog(this);
                if (javaDialog.SelectedIndices == null)
                    return;

                foreach (var idx in javaDialog.SelectedIndices)
                {
                    var javaNode = available[idx];
                    javaNode.IsQueued = true;
                    _installQueue.Enqueue((javaNode, false));
                }
            }
        }

        var requiredBytes = (node.DownloadSizeMb + node.InstallSizeMb) * 1_048_576;
        if (requiredBytes > 0)
        {
            while (true)
            {
                var needed = (long)(requiredBytes * 1.1);
                var free = AppDiskUsageService.GetAvailableFreeSpace(appsBaseDir);
                if (free < needed)
                {
                    var choice = await ShowDiskSpaceDialog(node, node.Name, needed, free);
                    if (choice == "Retry")
                        continue;
                    foreach (var item in _installQueue)
                        item.Node.IsQueued = false;
                    _installQueue.Clear();
                    return;
                }

                break;
            }
        }

        var wasInstalled = node.IsInstalled;
        _downloading = true;
        _inSetupPhase = false;
        _installCts = new CancellationTokenSource();
        _activeNode = node;
        _activeDownloadFile = downloadFile;
        node.IsBeingInstalled = true;
        SetInstalling(true);
        Cursor = new Cursor(StandardCursorType.Wait);
        ShowDownloadBar(true);
        DownloadSizeText.Text = $"Preparing {node.Name}...";
        DownloadSpeedText.Text = "Please wait";

        try
        {
            var url = node.DownloadPath.TrimEnd('/') + "/" + downloadFile;
            var progressCts = new CancellationTokenSource();
            var progressToken = progressCts.Token;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (progressToken.IsCancellationRequested)
                    return;
                if (p.Percent > 0)
                {
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = p.Percent;
                    DownloadSizeText.Text = p.FormatReceived();
                    DownloadSpeedText.Text = p.BytesPerSecond > 0 ? p.FormatSpeed() : string.Empty;
                }
                else
                {
                    DownloadProgressBar.IsIndeterminate = true;
                }
            });

            string localPath;
            var preferred = MirrorService.LoadPreferredMirror(url);
            var downloadUrl = preferred != null
                ? MirrorService.ApplyMirror(url, preferred)
                : url;
            while (true)
            {
                try
                {
                    localPath = await _downloadService.DownloadAsync(downloadUrl, downloadFile, progress, node.UserAgent, _installCts.Token);
                    await progressCts.CancelAsync();
                    progressCts.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    if (_installCts.IsCancellationRequested)
                        return;
                    try
                    {
                        File.Delete(Path.Combine(appsBaseDir, downloadFile));
                    }
                    catch
                    {
                        /* partial download file may not exist yet if the connection failed early */
                    }

                    var failed = MirrorService.GetCurrentMirrorSlug(downloadUrl);
                    var available = MirrorService.GetAvailableMirrors(downloadUrl);
                    if (available.Count > 0)
                    {
                        ShowDownloadBar(false);
                        var mirrorDialog = new MirrorDialog(node.Name, failed, available) { Icon = new WindowIcon(node.Icon) };
                        await mirrorDialog.ShowDialog(this);
                        if (mirrorDialog.SelectedMirror != null)
                        {
                            MirrorService.SavePreferredMirror(downloadUrl, mirrorDialog.SelectedMirror);
                            downloadUrl = MirrorService.ApplyMirror(downloadUrl, mirrorDialog.SelectedMirror);
                            ShowDownloadBar(true);
                            DownloadSizeText.Text = $"Preparing {node.Name}...";
                            continue;
                        }
                    }

                    await ShowDialog(node, "Download Failed", ex.Message, "OK");
                    return;
                }
            }

            var hash = AppDownloadService.VerifyHash(localPath, downloadHash);
            if (hash == HashResult.Invalid)
            {
                ShowDownloadBar(false);
                var choice = await ShowDialog(
                    node, "Hash Mismatch",
                    "The downloaded file's hash does not match the expected value.\n\n" +
                    "The file may be corrupted or tampered with.",
                    "Scan with VirusTotal", "Proceed Anyway", "Cancel");
                if (choice == "Scan with VirusTotal")
                {
                    await new VirusTotalDialog(node) { Icon = new WindowIcon(node.Icon) }.ShowDialog(this);
                    choice = await ShowDialog(
                        node, "Hash Mismatch",
                        "Do you want to proceed with the installation?",
                        "Proceed", "Cancel");
                    if (choice != "Proceed")
                    {
                        File.Delete(localPath);
                        return;
                    }
                }
                else if (choice != "Proceed Anyway")
                {
                    File.Delete(localPath);
                    return;
                }

                ShowDownloadBar(true);
            }

            var sevenZipPath = AppDownloadService.FindSevenZip(appsBaseDir);
            var isLegacyArchive =
                sevenZipPath != null &&
                DateTime.TryParse(node.UpdateDate, out var updateDtCheck) &&
                updateDtCheck.Date < new DateTime(2016, 1, 1);

            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressBar.Value = 0;
            DownloadSizeText.Text = isLegacyArchive
                ? $"Extracting {node.Name}..."
                : $"Installing {node.Name}...";
            DownloadSpeedText.Text = "Please wait";
            _inSetupPhase = true;

            try
            {
                if (isLegacyArchive)
                {
                    var extractDest = Path.Combine(appsBaseDir, node.SectionName);
                    await AppDownloadService.ExtractAsync(sevenZipPath!, localPath, extractDest, _installCts.Token);
                    try
                    {
                        File.Delete(localPath);
                    }
                    catch
                    {
                        /* file may be locked briefly after extraction completes */
                    }

                    try
                    {
                        var pluginsDir = Path.Combine(extractDest, "$PLUGINSDIR");
                        if (Directory.Exists(pluginsDir))
                            Directory.Delete(pluginsDir, true);
                    }
                    catch
                    {
                        /* $PLUGINSDIR cleanup is best-effort; leftover files are harmless */
                    }

                    try
                    {
                        SetIniSectionValue(
                            Path.Combine(extractDest, "App", "AppInfo", "appinfo.ini"),
                            "PortableApps.comInstaller",
                            "InstallIntegrityCheck",
                            "true");
                    }
                    catch
                    {
                        /* appinfo.ini write is best-effort; platform will re-create it on next run */
                    }
                }
                else
                {
                    try
                    {
                        var installDir = node.IsPlugin
                            ? PluginService.GetInstallDir(node.SectionName)
                            : Path.Combine(appsBaseDir, node.SectionName);
                        var licenseDir = Path.Combine(installDir, "Data", "PortableApps.comInstaller");
                        Directory.CreateDirectory(licenseDir);
                        await File.WriteAllTextAsync(
                            Path.Combine(licenseDir, "license.ini"),
                            "[PortableApps.comInstaller]\nEULAVersion=1\n",
                            _installCts.Token);
                    }
                    catch
                    {
                        /* non-critical – installer may prompt for EULA if this fails */
                    }

                    await AppDownloadService.ExecuteAsync(localPath, node.SectionName, appsBaseDir, false, _installCts.Token);

                    try
                    {
                        var baseDir = node.IsPlugin
                            ? PluginService.GetInstallDir(node.SectionName)
                            : Path.Combine(appsBaseDir, node.SectionName);
                        var appInfoPath = Path.Combine(baseDir, "App", "AppInfo", "appinfo.ini");
                        var eulaInstallerDir = Path.Combine(baseDir, "Data", "PortableApps.comInstaller");
                        if (!ReadEulaVersion(appInfoPath) && Directory.Exists(eulaInstallerDir))
                            Directory.Delete(eulaInstallerDir, true);
                    }
                    catch
                    {
                        /* eulaInstallerDir removal is best-effort; stale folder is harmless */
                    }
                }

                string? appExeAfter;
                if (node.IsPlugin)
                {
                    var m = PluginService.GetMarkerFile(node.SectionName);
                    appExeAfter = File.Exists(m) ? m : null;
                }
                else
                {
                    appExeAfter = await ResolveAppExeAsync(node, appsBaseDir);
                }

                if (appExeAfter != null)
                {
                    if (chosenLanguage != null)
                        AppLanguageService.Save(node.SectionName, chosenLanguage);
                    node.IsInstalled = true;
                    var installDir = node.IsPlugin
                        ? PluginService.GetInstallDir(node.SectionName)
                        : Path.Combine(appsBaseDir, node.SectionName);
                    _ = ScanAndCacheNodeSizeAsync(node, installDir);
                    if (!node.IsPlugin && AppBackupService.HasBackup(node.SectionName))
                        try
                        {
                            AppBackupService.RestoreBackup(node.SectionName, installDir);
                        }
                        catch (Exception ex)
                        {
                            SettingsService.Log($"Failed to restore backup for '{node.SectionName}': {ex.Message}");
                        }

                    if (DateTime.TryParse(node.UpdateDate, out var updateDate))
                    {
                        File.SetLastWriteTime(appExeAfter, updateDate);
                        node.CurrentDate = updateDate.ToString("yyyy-MM-dd");
                    }
                    else
                    {
                        node.CurrentDate = File.GetLastWriteTime(appExeAfter).ToString("yyyy-MM-dd");
                    }

                    if (launch && !node.IsPlugin)
                        await TryLaunchWithArgsAsync(node);
                }
            }
            catch (Exception ex)
            {
                if (!_installCts.IsCancellationRequested)
                    await ShowDialog(node, "Launch Failed", ex.Message, "OK");
            }
        }
        finally
        {
            _inSetupPhase = false;
            node.IsBeingInstalled = false;

            if (_installCts?.IsCancellationRequested == true)
            {
                var downloadedFile = Path.Combine(appsBaseDir, _activeDownloadFile ?? string.Empty);
                try
                {
                    File.Delete(downloadedFile);
                }
                catch
                {
                    /* already gone or never created */
                }

                if (!wasInstalled)
                {
                    var appDir = node.IsPlugin
                        ? PluginService.GetInstallDir(node.SectionName)
                        : Path.Combine(appsBaseDir, node.SectionName);
                    try
                    {
                        if (Directory.Exists(appDir))
                            Directory.Delete(appDir, true);
                    }
                    catch
                    {
                        /* partial dir may be locked */
                    }
                }
            }

            _activeNode = null;
            _activeDownloadFile = null;
            _installCts?.Dispose();
            _installCts = null;

            ShowDownloadBar(false);

            if (_installQueue.TryDequeue(out var next))
            {
                next.Node.IsQueued = false;
                _ = InstallApp(next.Node, appsBaseDir, next.Launch, true);
            }
            else
            {
                _downloading = false;
                Cursor = Cursor.Default;
                SetInstalling(false);
            }
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

    private static void SetIniSectionValue(string filePath, string section, string key, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var lines = File.Exists(filePath) ? File.ReadAllLines(filePath).ToList() : [];
        var header = $"[{section}]";
        var sectionIdx = lines.FindIndex(l =>
                                             l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));

        if (sectionIdx < 0)
        {
            lines.Add(header);
            lines.Add($"{key}={value}");
        }
        else
        {
            var keyPrefix = key + "=";
            var keyIdx = -1;
            for (var i = sectionIdx + 1; i < lines.Count; i++)
            {
                if (lines[i].Trim().StartsWith('['))
                    break;
                if (!lines[i].Trim().StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                keyIdx = i;
                break;
            }

            if (keyIdx >= 0)
                lines[keyIdx] = $"{key}={value}";
            else
                lines.Insert(sectionIdx + 1, $"{key}={value}");
        }

        File.WriteAllLines(filePath, lines);
    }

    /// Returns true when appinfo.ini declares EULAVersion > 0 under [License].
    private static bool ReadEulaVersion(string appInfoPath)
    {
        if (!File.Exists(appInfoPath))
            return false;
        var inLicense = false;
        foreach (var line in File.ReadLines(appInfoPath))
        {
            var t = line.Trim();
            if (t.StartsWith('['))
            {
                inLicense = t.Equals("[License]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inLicense)
                continue;
            var eq = t.IndexOf('=');
            if (eq <= 0)
                continue;
            if (!t[..eq].Trim().Equals("EULAVersion", StringComparison.OrdinalIgnoreCase))
                continue;
            return int.TryParse(t[(eq + 1)..].Trim(), out var v) && v > 0;
        }

        return false;
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
        UpdateThemeIcon();
        UpdateIconSizeButton();
        UpdateCategoryScopeButton();
        UpdateCategoryDisplayButton();
        UpdateInstallFilterButton();
        UpdateViewModeButton();
        UpdateFontSizeButton();
        _ = CheckForUpdateAsync();
        _systemIsDark = Application.Current!.ActualThemeVariant == ThemeVariant.Dark;
        Application.Current.ActualThemeVariantChanged += (_, _) =>
        {
            if (Application.Current.RequestedThemeVariant == null)
                _systemIsDark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
            UpdateThemeIcon();
        };
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
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is not { Key: Key.F, KeyModifiers: KeyModifiers.Control })
            return;
        SearchBox.Focus();
        e.Handled = true;
    }

    private void OnThemePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right)
            OnThemeToggle(sender, e);
    }

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        var current = Application.Current!.RequestedThemeVariant;
        var opposite = _systemIsDark ? ThemeVariant.Light : ThemeVariant.Dark;
        var same = _systemIsDark ? (ThemeVariant?)ThemeVariant.Dark : ThemeVariant.Light;

        ThemeVariant? next;
        if (current == opposite)
            next = null;
        else if (current == null && _prevTheme == opposite)
            next = same;
        else
            next = opposite;

        _prevTheme = current;
        Application.Current.RequestedThemeVariant = next;
        UpdateThemeIcon(next == null);
    }

    private void UpdateThemeIcon(bool showAutoIcon = false)
    {
        var requested = Application.Current!.RequestedThemeVariant;
        var isDark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
        ThemeToggleButton.Content =
            requested == ThemeVariant.Light ? "🌞" :
            requested == ThemeVariant.Dark ? "🌚" :
            showAutoIcon ? "🌗" :
            isDark ? "🌚" : "🌞";
        ApplyDarkTitlebar(isDark);
    }

    private void ApplyDarkTitlebar(bool dark)
    {
        Win32Window.ApplyDarkTitlebar(this, dark);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyDarkTitlebar(Application.Current?.ActualThemeVariant == ThemeVariant.Dark);
        _ = CheckOrphanedFilesAsync();
    }

    private async Task CheckOrphanedFilesAsync()
    {
        var appsDir = AppDownloadService.AppsDir;
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
        foreach (var node in vm.AllNodes.Where(n => !n.IsCustom))
            node.Icon = _iconManager.GetIcon(node.SectionName, size);
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
        vm.BeforeRebuildRows += () => _pendingScrollY = MainScroller.Offset.Y;
        vm.RowsFullyLoaded += () =>
        {
            if (_pendingScrollTop)
            {
                _pendingScrollTop = false;
                _pendingScrollY = null;
                MainScroller.Offset = new Vector(0, 0);
            }
            else if (_pendingScrollApp != null)
            {
                var app = _pendingScrollApp;
                _pendingScrollApp = null;
                _pendingScrollY = null;
                ScrollToApp(app);
            }
            else if (_pendingScrollTarget != null)
            {
                var target = _pendingScrollTarget;
                _pendingScrollTarget = null;
                _pendingScrollY = null;
                ScrollToSectionName(target);
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

    private void ScrollToSectionName(string sectionName)
    {
        if (DataContext is not MainViewModel vm)
            return;
        for (var i = 0; i < vm.FlatRows.Count; i++)
        {
            if (vm.FlatRows[i] is not AppNode node || node.SectionName != sectionName)
                continue;
            var container = ActiveList.ContainerFromIndex(i);
            if (container is null)
            {
                MainScroller.Offset = new Vector(0, MainScroller.Extent.Height);
                return;
            }

            var pos = container.TranslatePoint(new Point(0, 0), MainList);
            if (pos.HasValue)
                MainScroller.Offset = new Vector(0, pos.Value.Y - GetStickyOffset(vm));
            return;
        }

        MainScroller.Offset = new Vector(0, MainScroller.Extent.Height);
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
            if (vm.FlatRows[i] is not CategoryNode catHeader || !catHeader.HasCategory)
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
        var info = await AppSelfUpdater.CheckAsync(_cts.Token);
        if (info == null)
            return;
        _pendingUpdate = info;
        UpdateButton.IsVisible = true;
        UpdateButton.Content = $"Update {info.Version}";
    }

    private async void OnUpdateApp(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_pendingUpdate == null)
                return;
            UpdateButton.IsEnabled = false;
            ShowDownloadBar(true);
            DownloadProgressBar.IsIndeterminate = false;
            DownloadSizeText.Text = $"Downloading update {_pendingUpdate.Version}...";
            DownloadSpeedText.Text = string.Empty;

            try
            {
                var info = _pendingUpdate;
                var progress = new Progress<int>(p =>
                {
                    DownloadProgressBar.Value = p;
                    DownloadSizeText.Text = $"Downloading update {info.Version}... {p}%";
                });
                await AppSelfUpdater.ApplyAsync(info, progress, _cts.Token);
            }
            catch (Exception ex)
            {
                ShowDownloadBar(false);
                UpdateButton.IsEnabled = true;
                SettingsService.Log($"Self-update failed: {ex.Message}");
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
        if (sender is not AutoCompleteBox box || box.SelectedItem is not string name)
        {
            _activateOnSearchClose = false;
            return;
        }

        if (!string.Equals(box.Text, name, StringComparison.OrdinalIgnoreCase))
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
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && SearchBox.IsDropDownOpen)
        {
            _activateOnSearchClose = true;
            return;
        }

        if (e.Key == Key.Up && !SearchBox.IsDropDownOpen && _searchHistory.Count > 0)
        {
            _historyIndex = Math.Min(_historyIndex + 1, _searchHistory.Count - 1);
            SearchBox.Text = _searchHistory[_historyIndex];
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down && !SearchBox.IsDropDownOpen && _historyIndex >= 0)
        {
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
        var existing = SettingsService.Load();
        existing.ViewPresets.Remove("Default");
        SettingsService.Save(new AppSettings
        {
            InstallFilter = vm.InstallFilter,
            SortColumn = vm.Columns.SortColumn,
            SortDescending = vm.Columns.SortDescending,
            ColumnName = vm.Columns.Name,
            ColumnVersion = vm.Columns.Version,
            ColumnDownload = vm.Columns.Download,
            ColumnInstall = vm.Columns.Install,
            ColumnJoined = vm.Columns.Joined,
            ColumnUpdated = vm.Columns.Updated,
            ColumnUsed = vm.Columns.Used,
            Theme = theme == ThemeVariant.Light ? "Light" : theme == ThemeVariant.Dark ? "Dark" : "Default",
            ViewPresets = existing.ViewPresets
        });
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
        vm.CategoryScope = preset.CategoryScope;
        vm.Columns.FontSize = preset.FontSize;
        vm.Columns.IconSize = preset.IconSize;
        vm.Columns.IsGridView = preset.IsGridView;

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

            if (_downloading)
            {
                e.Cancel = true;
                var appName = _activeNode?.Name ?? "the current app";
                var confirmDialog = new AppDialog(
                    "Installation in Progress",
                    $"{appName} is currently being installed.\n\n" +
                    "Closing now will abort the installation and may leave it in a corrupt state.\n\n" +
                    "Are you sure you want to close?",
                    "Close Anyway", "Keep Running");
                if (_activeNode != null)
                    confirmDialog.Icon = new WindowIcon(_activeNode.Icon);
                await confirmDialog.ShowDialog(this);
                if (confirmDialog.Result != "Close Anyway")
                    return;
                await _installCts?.CancelAsync()!;
                AppDownloadService.KillActiveInstaller();
                if (_activeDownloadFile is { } activeFile)
                {
                    var partialFile = Path.Combine(AppDownloadService.AppsDir, activeFile);
                    try
                    {
                        File.Delete(partialFile);
                    }
                    catch
                    {
                        /* file may not exist yet or still locked by the installer */
                    }
                }

                foreach (var item in _installQueue)
                    item.Node.IsQueued = false;
                _installQueue.Clear();
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
        _installCts?.Dispose();
        _iconManager.Dispose();
        _appDatabaseUpdater.Dispose();
        _downloadService.Dispose();
        base.OnClosed(e);
    }
}