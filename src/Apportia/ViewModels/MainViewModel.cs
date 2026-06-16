using System.ComponentModel;
using System.Runtime.CompilerServices;
using Apportia.Models;
using Apportia.Services;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Apportia.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly CategoryNode _advancedCategoryNode;
    private readonly List<CategoryNode> _grouped = [];
    private readonly CategoryNode _legacyCategoryNode;
    private CategoryDisplayMode _categoryDisplay = CategoryDisplayMode.Full;
    private CategoryScope _categoryScope = CategoryScope.Standard;

    private bool _hasInstalledApps;
    private InstallFilter _installFilter = InstallFilter.All;
    private CancellationTokenSource _rebuildCts = new();

    public MainViewModel(IReadOnlyList<AppEntry> entries, IconManager iconManager, int iconSize = 24)
    {
        var visible = entries.Where(e => !string.Equals(e.Category, "None", StringComparison.OrdinalIgnoreCase)).ToList();
        // Group by real category (advanced apps are only shown under "Advanced" when not installed)
        var byCategory =
            visible.GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                   .OrderBy(g => string.Equals(g.Key, "Games", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                   .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byCategory)
        {
            var nodes = group.Select(entry =>
                             {
                                 bool exists;
                                 string currentDate;
                                 if (PluginService.IsPlugin(entry.SectionName))
                                 {
                                     var marker = PluginService.GetMarkerFile(entry.SectionName);
                                     exists = File.Exists(marker);
                                     currentDate = exists ? File.GetLastWriteTime(marker).ToString("yyyy-MM-dd") : string.Empty;
                                 }
                                 else
                                 {
                                     var appDir = AppDownloadService.GetInstallDir(entry.SectionName);
                                     var (resolvedExe, candidates) = AppExecutableService.Resolve(appDir, entry.SectionName);
                                     exists = resolvedExe != null || candidates.Length > 0;
                                     currentDate = resolvedExe != null
                                         ? File.GetLastWriteTime(resolvedExe).ToString("yyyy-MM-dd")
                                         : string.Empty;
                                 }

                                 var node = new AppNode(
                                     entry,
                                     iconManager.GetIcon(entry.SectionName, iconSize),
                                     Columns,
                                     exists,
                                     false,
                                     PluginService.IsPlugin(entry.SectionName),
                                     currentDate);
                                 node.PropertyChanged += OnNodePropertyChanged;
                                 return node;
                             })
                             .ToList();

            var catNode = new CategoryNode(group.Key, Columns);
            catNode.PropertyChanged += OnCategoryPropertyChanged;
            foreach (var n in nodes)
                catNode.Nodes.Add(n);
            _grouped.Add(catNode);
        }

        foreach (var entry in CustomAppService.LoadAll())
        {
            var node = new AppNode(entry, iconManager.GetCustomIcon(entry.SectionName), Columns, true, true);
            node.PropertyChanged += OnNodePropertyChanged;
            var group = _grouped.FirstOrDefault(g => g.Category == entry.Category);
            if (group == null)
            {
                var catNode = new CategoryNode(entry.Category, Columns);
                catNode.PropertyChanged += OnCategoryPropertyChanged;
                catNode.Nodes.Add(node);
                _grouped.Add(catNode);
            }
            else
            {
                group.Nodes.Add(node);
            }
        }

        _advancedCategoryNode = new CategoryNode("Advanced", Columns);
        _advancedCategoryNode.PropertyChanged += OnCategoryPropertyChanged;
        _legacyCategoryNode = new CategoryNode("Legacy", Columns);
        _legacyCategoryNode.PropertyChanged += OnCategoryPropertyChanged;

        UpdateHasInstalledApps();
        UpdateShowCurrentColumn();

        var diskCache = AppDiskUsageService.LoadCache();
        foreach (var node in AllNodes.Where(n => n.IsInstalled))
        {
            if (diskCache.Sizes.TryGetValue(node.SectionName, out var bytes))
                node.SetUsedBytes(bytes);
        }

        _ = ScanDiskUsageAsync(diskCache);

        RebuildRows();
    }

    public ColumnWidths Columns { get; } = new();
    public AvaloniaList<object> FlatRows { get; } = [];
    public IEnumerable<AppNode> AllNodes => _grouped.SelectMany(g => g.Nodes);

    public IReadOnlyList<string> Categories =>
        _grouped.Select(g => g.Category)
                .Distinct()
                .Order()
                .ToList();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> SubCategoriesMap =>
        _grouped.ToDictionary(
            g => g.Category, IReadOnlyList<string> (g) => g.Nodes
                                                           .Select(n => n.SubCategory)
                                                           .Where(s => !string.IsNullOrEmpty(s))
                                                           .Distinct(StringComparer.OrdinalIgnoreCase)
                                                           .Order()
                                                           .ToList(),
            StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> AppNames
    {
        get;
        private set
        {
            field = value;
            Notify();
            SearchPlaceholder = $"Search {value.Count} apps...";
        }
    } = [];

    public string SearchPlaceholder
    {
        get;
        private set
        {
            field = value;
            Notify();
        }
    } = "Search apps...";

    public CategoryDisplayMode CategoryDisplay
    {
        get => _categoryDisplay;
        set
        {
            _categoryDisplay = value;
            Notify();
            RebuildRows();
        }
    }

    public InstallFilter InstallFilter
    {
        get => _installFilter;
        set
        {
            _installFilter = value;
            Columns.HighlightInstalled = value == InstallFilter.All;
            Columns.ShowUsedColumn = value == InstallFilter.Installed;
            UpdateShowMetaColumns();
            Notify();
            RebuildRows();
        }
    }

    public CategoryScope CategoryScope
    {
        get => _categoryScope;
        set
        {
            _categoryScope = value;
            Notify();
            RebuildRows();
        }
    }

    public bool HasInstalledApps
    {
        get => _hasInstalledApps;
        private set
        {
            _hasInstalledApps = value;
            Notify();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppNode AddCustomApp(AppEntry entry, Bitmap icon)
    {
        var node = new AppNode(entry, icon, Columns, true, true);
        node.PropertyChanged += OnNodePropertyChanged;

        var category = entry.Category;
        var group = _grouped.FirstOrDefault(g => g.Category == category);
        if (group == null)
        {
            var catNode = new CategoryNode(category, Columns);
            catNode.PropertyChanged += OnCategoryPropertyChanged;
            catNode.Nodes.Add(node);
            _grouped.Add(catNode);
        }
        else
        {
            group.Nodes.Add(node);
        }

        UpdateHasInstalledApps();
        RebuildRows();
        return node;
    }

    public void RemoveCustomApp(AppNode node)
    {
        if (_grouped.Any(group => group.Nodes.Remove(node)))
            node.PropertyChanged -= OnNodePropertyChanged;

        var toRemove = _grouped.Where(g => g.Nodes.Count == 0).ToList();
        foreach (var g in toRemove)
        {
            g.PropertyChanged -= OnCategoryPropertyChanged;
            _grouped.Remove(g);
        }

        UpdateHasInstalledApps();
        RebuildRows();
    }

    public bool EnsureAppVisible(string appName)
    {
        var appNode = AllNodes.FirstOrDefault(n => n.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
        if (appNode == null)
            return false;

        CategoryNode? catNode;
        switch (appNode)
        {
            case { IsAdvanced: true, IsInstalled: false }
                when _categoryDisplay != CategoryDisplayMode.None && _categoryScope != CategoryScope.Standard:
                catNode = _advancedCategoryNode;
                break;
            case { IsLegacy: true, IsInstalled: false }
                when _categoryDisplay != CategoryDisplayMode.None && _categoryScope == CategoryScope.Full:
                catNode = _legacyCategoryNode;
                break;
            default:
                catNode = _grouped.FirstOrDefault(g => g.Nodes.Contains(appNode));
                break;
        }

        if (catNode == null)
            return false;

        var changed = false;
        if (!catNode.IsExpanded)
        {
            catNode.IsExpanded = true;
            changed = true;
        }

        if (string.IsNullOrEmpty(appNode.SubCategory) || _categoryDisplay != CategoryDisplayMode.Full)
            return changed;
        var subNode = catNode.GetOrCreateSubCategory(appNode.SubCategory);
        if (subNode.IsExpanded)
            return changed;
        subNode.IsExpanded = true;
        changed = true;

        return changed;
    }

    public void SortBy(string column)
    {
        var descending = Columns.SortColumn == column && !Columns.SortDescending;
        Columns.SetSort(column, descending);
        RebuildRows();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppNode.NeedsUpdate))
        {
            UpdateShowCurrentColumn();
            return;
        }

        if (e.PropertyName != nameof(AppNode.IsInstalled))
            return;
        UpdateHasInstalledApps();
        if (_installFilter != InstallFilter.All ||
            sender is AppNode { IsAdvanced: true } or AppNode { IsLegacy: true } ||
            _categoryScope == CategoryScope.Standard && sender is AppNode { Category: "Games" })
            RebuildRows();
    }

    private void OnCategoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryNode.IsExpanded))
            RebuildRows();
    }

    private void UpdateHasInstalledApps()
    {
        HasInstalledApps = _grouped.SelectMany(g => g.Nodes).Any(n => n.IsInstalled);
        if (!_hasInstalledApps && _installFilter == InstallFilter.Installed)
            InstallFilter = InstallFilter.All;
    }

    private void UpdateShowCurrentColumn()
    {
        Columns.ShowCurrentColumn = _grouped.SelectMany(g => g.Nodes).Any(n => n.NeedsUpdate);
        UpdateShowMetaColumns();
    }

    private void UpdateShowMetaColumns()
    {
        Columns.ShowMetaColumns = _installFilter != InstallFilter.Installed || Columns.ShowCurrentColumn;
    }

    private void RebuildAppNames()
    {
        var hideGames = _categoryScope == CategoryScope.Standard;
        var visible = new List<AppNode>();

        foreach (var catNode in _grouped)
        {
            var isGames = string.Equals(catNode.Category, "Games", StringComparison.OrdinalIgnoreCase);
            visible.AddRange(catNode.Nodes.Where(n =>
                                                     n.IsInstalled || n is { IsAdvanced: false, IsLegacy: false } && !(isGames && hideGames)));
        }

        if (_categoryScope != CategoryScope.Standard)
            visible.AddRange(_grouped.SelectMany(g => g.Nodes)
                                     .Where(n => n is { IsAdvanced: true, IsInstalled: false }));

        if (_categoryScope == CategoryScope.Full)
            visible.AddRange(_grouped.SelectMany(g => g.Nodes)
                                     .Where(n => n is { IsLegacy: true, IsInstalled: false }));

        if (_installFilter == InstallFilter.Installed)
            visible.RemoveAll(n => !n.IsInstalled);
        else if (_installFilter == InstallFilter.NotInstalled)
            visible.RemoveAll(n => n.IsInstalled);

        AppNames = visible.Select(n => n.Name).Order().ToList();
    }

    public event Action? RowsFullyLoaded;

    private void RebuildRows()
    {
        _rebuildCts.Cancel();
        _rebuildCts = new CancellationTokenSource();
        var ct = _rebuildCts.Token;

        RebuildAppNames();
        var rows = BuildRowsList();

        FlatRows.Clear();
        if (rows.Count == 0)
            return;

        const int firstBatch = 40;
        FlatRows.AddRange(rows[..Math.Min(firstBatch, rows.Count)]);

        if (rows.Count > firstBatch)
            _ = AddRemainingRowsAsync(rows, firstBatch, ct);
        else
            RowsFullyLoaded?.Invoke();
    }

    private async Task AddRemainingRowsAsync(List<object> rows, int startIndex, CancellationToken ct)
    {
        const int batchSize = 40;
        for (var i = startIndex; i < rows.Count; i += batchSize)
        {
            if (ct.IsCancellationRequested)
                return;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            if (ct.IsCancellationRequested)
                return;
            FlatRows.AddRange(rows[i..Math.Min(i + batchSize, rows.Count)]);
        }

        if (!ct.IsCancellationRequested)
        {
            // One extra yield so layout (higher priority) runs before signalling
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            if (!ct.IsCancellationRequested)
                RowsFullyLoaded?.Invoke();
        }
    }

    private List<object> BuildRowsList()
    {
        var rows = new List<object>();

        if (_categoryDisplay != CategoryDisplayMode.None)
        {
            var hideGames = _categoryScope == CategoryScope.Standard;
            var withSubs = _categoryDisplay == CategoryDisplayMode.Full;

            foreach (var catNode in _grouped)
            {
                var isGames = string.Equals(catNode.Category, "Games", StringComparison.OrdinalIgnoreCase);
                var visible = Filter(catNode.Nodes.Where(n =>
                                                             n.IsInstalled ||
                                                             n is { IsAdvanced: false, IsLegacy: false } && !(isGames && hideGames)).ToList());
                if (visible.Count == 0)
                    continue;
                rows.Add(catNode);
                if (!catNode.IsExpanded)
                    continue;
                if (withSubs)
                    AddWithSubCategories(rows, catNode, Sort(visible));
                else
                    AddFlat(rows, Sort(visible));
            }

            if (_categoryScope != CategoryScope.Standard)
            {
                var advanced = Filter(_grouped
                                      .SelectMany(g => g.Nodes)
                                      .Where(n => n is { IsAdvanced: true, IsInstalled: false })
                                      .ToList());
                if (advanced.Count > 0)
                {
                    rows.Add(_advancedCategoryNode);
                    if (_advancedCategoryNode.IsExpanded)
                    {
                        if (withSubs)
                            AddWithSubCategories(rows, _advancedCategoryNode, Sort(advanced));
                        else
                            AddFlat(rows, Sort(advanced));
                    }
                }
            }

            if (_categoryScope == CategoryScope.Full)
            {
                var legacy = Filter(_grouped
                                    .SelectMany(g => g.Nodes)
                                    .Where(n => n is { IsLegacy: true, IsInstalled: false })
                                    .ToList());
                if (legacy.Count <= 0)
                    return rows;
                rows.Add(_legacyCategoryNode);
                if (!_legacyCategoryNode.IsExpanded)
                    return rows;
                if (withSubs)
                    AddWithSubCategories(rows, _legacyCategoryNode, Sort(legacy));
                else
                    AddFlat(rows, Sort(legacy));
            }
        }
        else
        {
            var hideGames = _categoryScope == CategoryScope.Standard;
            var allNodes = _grouped.SelectMany(g =>
            {
                var isGames = string.Equals(g.Category, "Games", StringComparison.OrdinalIgnoreCase);
                return g.Nodes.Where(n => n.IsInstalled || n is { IsAdvanced: false, IsLegacy: false } && !(isGames && hideGames));
            }).ToList();

            if (_categoryScope != CategoryScope.Standard)
                allNodes.AddRange(
                    _grouped.SelectMany(g => g.Nodes).Where(n => n is { IsAdvanced: true, IsInstalled: false }));

            if (_categoryScope == CategoryScope.Full)
                allNodes.AddRange(
                    _grouped.SelectMany(g => g.Nodes).Where(n => n is { IsLegacy: true, IsInstalled: false }));

            AddFlat(rows, Sort(Filter(allNodes)));
        }

        return rows;
    }

    private static void AddFlat(List<object> rows, IEnumerable<AppNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsIndented = false;
            rows.Add(node);
        }
    }

    private static void AddWithSubCategories(List<object> rows, CategoryNode catNode, IEnumerable<AppNode> nodes)
    {
        var grouped =
            nodes.GroupBy(n => n.SubCategory, StringComparer.OrdinalIgnoreCase)
                 .OrderBy(g => string.IsNullOrEmpty(g.Key) ? 0 : 1)
                 .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                 .ToList();

        var hasAnySub = grouped.Any(g => !string.IsNullOrEmpty(g.Key));

        foreach (var group in grouped)
        {
            var isSubGroup = hasAnySub && !string.IsNullOrEmpty(group.Key);
            if (isSubGroup)
            {
                var subNode = catNode.GetOrCreateSubCategory(group.Key);
                rows.Add(subNode);
                if (!subNode.IsExpanded)
                    continue;
            }

            foreach (var node in group)
            {
                node.IsIndented = isSubGroup;
                rows.Add(node);
            }
        }
    }

    private List<AppNode> Filter(List<AppNode> nodes)
    {
        return _installFilter switch
        {
            InstallFilter.Installed => nodes.Where(n => n.IsInstalled).ToList(),
            InstallFilter.NotInstalled => nodes.Where(n => !n.IsInstalled).ToList(),
            _ => nodes
        };
    }

    private IEnumerable<AppNode> Sort(List<AppNode> nodes)
    {
        var sorted = Columns.SortColumn switch
        {
            "Name" => nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
            "Version" => nodes.OrderBy(n => n.PackageVersion, StringComparer.OrdinalIgnoreCase),
            "Download" => nodes.OrderBy(n => n.DownloadSizeMb),
            "Install" => nodes.OrderBy(n => n.InstallSizeMb),
            "Joined" => nodes.OrderBy(n => ParseDate(n.JoinedDate)),
            "Updated" => nodes.OrderBy(n => ParseDate(n.UpdateDate)),
            "Used" => nodes.OrderBy(n => n.UsedBytes),
            _ => nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
        };
        return Columns.SortDescending ? sorted.Reverse() : sorted;
    }


    private static DateTime ParseDate(string s)
    {
        return DateTime.TryParse(s, out var d) ? d : DateTime.MinValue;
    }

    private async Task ScanDiskUsageAsync(DiskUsageCache cache)
    {
        var customDir = CustomAppService.CustomAppsDir;
        var apps = AllNodes
                   .Where(n => n.IsInstalled)
                   .Select(n => (
                               n.SectionName,
                               Dir: n.IsCustom
                                   ? Path.Combine(customDir, n.SectionName)
                                   : n.IsPlugin
                                       ? PluginService.GetInstallDir(n.SectionName)
                                       : AppDownloadService.GetInstallDir(n.SectionName)))
                   .ToList();

        await foreach (var (sectionName, bytes) in AppDiskUsageService.ScanAllAsync(apps))
        {
            cache.Sizes[sectionName] = bytes;
            var node = AllNodes.FirstOrDefault(n => n.SectionName == sectionName);
            if (node != null)
                await Dispatcher.UIThread.InvokeAsync(() => node.SetUsedBytes(bytes));
        }

        AppDiskUsageService.SaveCache(cache);
    }

    private void Notify([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}