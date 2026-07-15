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
    private readonly AppImageManager _iconManager;
    private readonly int _iconSize;
    private readonly CategoryNode _legacyCategoryNode;
    private int _batchDepth;
    private bool _batchDirty;
    private CategoryDisplayMode _categoryDisplay = CategoryDisplayMode.Full;
    private CategoryScope _categoryScope = CategoryScope.Standard;
    private bool _gridActive;

    private int _gridColumns = 1;

    private bool _hasInstalledApps;
    private InstallFilter _installFilter = InstallFilter.All;
    private CancellationTokenSource _rebuildCts = new();
    private List<AppNode> _visibleNodes = [];

    public MainViewModel(IReadOnlyList<AppEntry> entries, AppImageManager iconManager, int iconSize = 24)
    {
        _iconManager = iconManager;
        _iconSize = iconSize;
        var visible = entries.Where(e => !string.Equals(e.Category, "None", StringComparison.OrdinalIgnoreCase)).ToList();
        var byCategory =
            visible.GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                   .OrderBy(g => string.Equals(g.Key, "Games", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                   .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var localVersions = LocalVersionService.Load();
        var installed = CurrentAppService.LoadAll();

        foreach (var group in byCategory)
        {
            var nodes = group.Select(entry =>
                             {
                                 var isPlugin = PluginService.IsPlugin(entry.SectionName);
                                 var exists = installed.ContainsKey(entry.SectionName);

                                 var node = new AppNode(
                                     entry,
                                     iconManager.GetIcon(entry.SectionName, Math.Max(16, iconSize)),
                                     Columns,
                                     exists,
                                     false,
                                     isPlugin);
                                 if (exists && localVersions.TryGetValue(entry.SectionName, out var lv))
                                 {
                                     node.LocalDisplayVersion = lv.DisplayVersion;
                                     node.LocalPackageVersion = lv.PackageVersion;
                                 }

                                 node.PropertyChanged += OnNodePropertyChanged;
                                 return node;
                             })
                             .ToList();

            var catNode = new CategoryNode(group.Key, Columns);
            catNode.PropertyChanged += OnCategoryPropertyChanged;
            catNode.SubCategoryExpansionChanged += OnSubCategoryExpansionChanged;
            foreach (var n in nodes)
                catNode.Nodes.Add(n);
            _grouped.Add(catNode);
        }

        foreach (var entry in CustomAppService.LoadAll())
        {
            var node = new AppNode(entry, iconManager.GetCustomIcon(entry.SectionName, Math.Max(16, iconSize)), Columns, true, true);
            node.PropertyChanged += OnNodePropertyChanged;
            var group = _grouped.FirstOrDefault(g => string.Equals(g.Category, entry.Category, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                var catNode = new CategoryNode(entry.Category, Columns);
                catNode.PropertyChanged += OnCategoryPropertyChanged;
                catNode.SubCategoryExpansionChanged += OnSubCategoryExpansionChanged;
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
    public AvaloniaList<object> GridRows { get; } = [];

    public int GridColumns
    {
        get => _gridColumns;
        set
        {
            var v = Math.Max(1, value);
            if (_gridColumns == v) return;
            _gridColumns = v;
            if (_gridActive) RebuildGridRows();
        }
    }

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
            RequestRebuild();
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
            RequestRebuild();
        }
    }

    public CategoryScope CategoryScope
    {
        get => _categoryScope;
        set
        {
            _categoryScope = value;
            Notify();
            if (_batchDepth > 0)
            {
                _batchDirty = true;
                return;
            }

            if (_categoryDisplay == CategoryDisplayMode.None)
                RebuildRows();
            else
                UpdateScopeTailInPlace();
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

    public bool IsBuildingRows { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetGridActive(bool active)
    {
        if (_gridActive == active) return;
        _gridActive = active;
        if (active) RebuildGridRows();
        else GridRows.Clear();
    }

    private void NotifyRowsFullyLoaded()
    {
        if (_gridActive) RebuildGridRows();
        RowsFullyLoaded?.Invoke();
    }

    public void RebuildGridRows()
    {
        var cols = Math.Max(1, _gridColumns);
        var newRows = new List<object>(FlatRows.Count);
        var tileBatch = new List<AppNode>(cols);
        foreach (var row in FlatRows)
        {
            if (row is AppNode app)
            {
                tileBatch.Add(app);
                if (tileBatch.Count >= cols)
                {
                    newRows.Add(new GridTileRow(tileBatch.ToArray()));
                    tileBatch.Clear();
                }
            }
            else
            {
                if (tileBatch.Count > 0)
                {
                    newRows.Add(new GridTileRow(tileBatch.ToArray()));
                    tileBatch.Clear();
                }

                newRows.Add(row);
            }
        }

        if (tileBatch.Count > 0)
            newRows.Add(new GridTileRow(tileBatch.ToArray()));

        GridRows.Clear();
        GridRows.AddRange(newRows);
    }

    public void MergeUpstreamEntries(IReadOnlyList<AppEntry> entries)
    {
        var existing = AllNodes.ToDictionary(n => n.SectionName, StringComparer.OrdinalIgnoreCase);
        var iconSize = Math.Max(16, _iconSize);

        foreach (var entry in entries)
        {
            if (string.Equals(entry.Category, "None", StringComparison.OrdinalIgnoreCase))
                continue;
            if (existing.TryGetValue(entry.SectionName, out var existingNode))
            {
                if (!existingNode.IsCustom)
                    existingNode.ApplyUpstream(entry);
                continue;
            }

            var node = new AppNode(
                entry,
                _iconManager.GetIcon(entry.SectionName, iconSize),
                Columns,
                false,
                false,
                PluginService.IsPlugin(entry.SectionName));
            node.PropertyChanged += OnNodePropertyChanged;

            var group = _grouped.FirstOrDefault(g => string.Equals(g.Category, entry.Category, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                group = new CategoryNode(entry.Category, Columns);
                group.PropertyChanged += OnCategoryPropertyChanged;
                group.SubCategoryExpansionChanged += OnSubCategoryExpansionChanged;
                _grouped.Add(group);
            }

            group.Nodes.Add(node);
        }

        _grouped.Sort((a, b) =>
        {
            var aGames = string.Equals(a.Category, "Games", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var bGames = string.Equals(b.Category, "Games", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            return aGames != bGames
                ? aGames - bGames
                : StringComparer.OrdinalIgnoreCase.Compare(a.Category, b.Category);
        });

        UpdateShowCurrentColumn();
        Notify(nameof(Categories));
        Notify(nameof(SubCategoriesMap));
        // Merged entries aren't visible under Installed; skip the rebuild to avoid flicker.
        if (_installFilter == InstallFilter.Installed)
            return;

        BeforeRebuildRows?.Invoke();
        RebuildRows();
    }

    public AppNode AddCustomApp(AppEntry entry, Bitmap icon)
    {
        var node = new AppNode(entry, icon, Columns, true, true);
        node.PropertyChanged += OnNodePropertyChanged;

        var category = entry.Category;
        var group = _grouped.FirstOrDefault(g => string.Equals(g.Category, category, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            var catNode = new CategoryNode(category, Columns);
            catNode.PropertyChanged += OnCategoryPropertyChanged;
            catNode.SubCategoryExpansionChanged += OnSubCategoryExpansionChanged;
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
        if (_installFilter == InstallFilter.All &&
            sender is not (AppNode { IsAdvanced: true } or AppNode { IsLegacy: true }) &&
            !(_categoryScope == CategoryScope.Standard && sender is AppNode { Category: "Games" }))
            return;
        BeforeRebuildRows?.Invoke();
        RebuildRows();
    }

    private void OnCategoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryNode.IsExpanded) && sender is CategoryNode catNode)
            ToggleCategoryInPlace(catNode);
    }

    private void OnSubCategoryExpansionChanged(object? _, SubCategoryNode subNode)
    {
        ToggleSubCategoryInPlace(subNode);
    }

    private void ToggleSubCategoryInPlace(SubCategoryNode subNode)
    {
        if (IsBuildingRows)
        {
            RebuildRows();
            return;
        }

        _rebuildCts.Cancel();
        _rebuildCts = new CancellationTokenSource();
        var idx = -1;
        for (var i = 0; i < FlatRows.Count; i++)
        {
            if (FlatRows[i] != subNode) continue;
            idx = i;
            break;
        }

        if (idx < 0)
            return;

        if (!subNode.IsExpanded)
        {
            var end = idx + 1;
            while (end < FlatRows.Count && FlatRows[end] is not (CategoryNode or SubCategoryNode))
                end++;
            FlatRows.RemoveRange(idx + 1, end - idx - 1);
        }
        else
        {
            var items = new List<object>();
            var end = idx + 1;
            while (end < FlatRows.Count && FlatRows[end] is not (CategoryNode or SubCategoryNode))
                end++;
            // Rebuild only matters if items differ — but since expand means they were removed, insert fresh
            // Find which CategoryNode owns this SubCategoryNode by scanning backwards
            CategoryNode? owner = null;
            for (var i = idx - 1; i >= 0; i--)
            {
                if (FlatRows[i] is not CategoryNode cat) continue;
                owner = cat;
                break;
            }

            if (owner == null)
                return;
            var allItems = BuildCategoryItems(owner);
            var inSub = false;
            foreach (var item in allItems)
            {
                if (item == subNode)
                {
                    inSub = true;
                    continue;
                }

                if (inSub && item is SubCategoryNode) break;
                if (inSub) items.Add(item);
            }

            if (items.Count > 0)
                FlatRows.InsertRange(idx + 1, items);
        }

        NotifyRowsFullyLoaded();
    }

    private void ToggleCategoryInPlace(CategoryNode catNode)
    {
        if (IsBuildingRows)
        {
            RebuildRows();
            return;
        }

        _rebuildCts.Cancel();
        _rebuildCts = new CancellationTokenSource();
        var idx = -1;
        for (var i = 0; i < FlatRows.Count; i++)
        {
            if (FlatRows[i] != catNode) continue;
            idx = i;
            break;
        }

        if (idx < 0)
            return;

        if (!catNode.IsExpanded)
        {
            var end = idx + 1;
            while (end < FlatRows.Count && FlatRows[end] is not CategoryNode)
                end++;
            FlatRows.RemoveRange(idx + 1, end - idx - 1);
        }
        else
        {
            var items = BuildCategoryItems(catNode);
            if (items.Count > 0)
                FlatRows.InsertRange(idx + 1, items);
        }

        NotifyRowsFullyLoaded();
    }

    private void UpdateScopeTailInPlace()
    {
        if (IsBuildingRows)
        {
            RebuildRows();
            return;
        }

        _rebuildCts.Cancel();
        _rebuildCts = new CancellationTokenSource();
        RebuildAppNames();

        var tailStart = FlatRows.Count;
        for (var i = 0; i < FlatRows.Count; i++)
        {
            if (FlatRows[i] is not CategoryNode cat) continue;
            if (!string.Equals(cat.Category, "Games", StringComparison.OrdinalIgnoreCase) &&
                cat != _advancedCategoryNode && cat != _legacyCategoryNode)
                continue;
            tailStart = i;
            break;
        }

        FlatRows.RemoveRange(tailStart, FlatRows.Count - tailStart);

        var withSubs = _categoryDisplay == CategoryDisplayMode.Full;
        var hideGames = _categoryScope == CategoryScope.Standard;

        var gamesNode = _grouped.LastOrDefault(g => string.Equals(g.Category, "Games", StringComparison.OrdinalIgnoreCase));
        if (gamesNode != null)
        {
            var visible = Filter(gamesNode.Nodes.Where(n =>
                                                           n.IsInstalled || n is { IsAdvanced: false, IsLegacy: false } && !hideGames).ToList());
            if (visible.Count > 0)
            {
                var rows = new List<object> { gamesNode };
                if (gamesNode.IsExpanded)
                {
                    if (withSubs) AddWithSubCategories(rows, gamesNode, Sort(visible));
                    else AddFlat(rows, Sort(visible));
                }

                FlatRows.AddRange(rows);
            }
        }

        if (_categoryScope != CategoryScope.Standard)
        {
            var advanced = Filter(_grouped.SelectMany(g => g.Nodes)
                                          .Where(n => n is { IsAdvanced: true, IsInstalled: false }).ToList());
            if (advanced.Count > 0)
            {
                var rows = new List<object> { _advancedCategoryNode };
                if (_advancedCategoryNode.IsExpanded)
                {
                    if (withSubs) AddWithSubCategories(rows, _advancedCategoryNode, Sort(advanced));
                    else AddFlat(rows, Sort(advanced));
                }

                FlatRows.AddRange(rows);
            }
        }

        if (_categoryScope == CategoryScope.Full)
        {
            var legacy = Filter(_grouped.SelectMany(g => g.Nodes)
                                        .Where(n => n is { IsLegacy: true, IsInstalled: false }).ToList());
            if (legacy.Count > 0)
            {
                var rows = new List<object> { _legacyCategoryNode };
                if (_legacyCategoryNode.IsExpanded)
                {
                    if (withSubs) AddWithSubCategories(rows, _legacyCategoryNode, Sort(legacy));
                    else AddFlat(rows, Sort(legacy));
                }

                FlatRows.AddRange(rows);
            }
        }

        NotifyRowsFullyLoaded();
    }

    private List<object> BuildCategoryItems(CategoryNode catNode)
    {
        var items = new List<object>();
        var hideGames = _categoryScope == CategoryScope.Standard;
        var withSubs = _categoryDisplay == CategoryDisplayMode.Full;

        List<AppNode> visible;
        if (catNode == _advancedCategoryNode)
        {
            visible = Filter(_grouped.SelectMany(g => g.Nodes)
                                     .Where(n => n is { IsAdvanced: true, IsInstalled: false })
                                     .ToList());
        }
        else if (catNode == _legacyCategoryNode)
        {
            visible = Filter(_grouped.SelectMany(g => g.Nodes)
                                     .Where(n => n is { IsLegacy: true, IsInstalled: false })
                                     .ToList());
        }
        else
        {
            var isGames = string.Equals(catNode.Category, "Games", StringComparison.OrdinalIgnoreCase);
            visible = Filter(catNode.Nodes.Where(n =>
                                                     n.IsInstalled || n is { IsAdvanced: false, IsLegacy: false } && !(isGames && hideGames)).ToList());
        }

        if (withSubs)
            AddWithSubCategories(items, catNode, Sort(visible));
        else
            AddFlat(items, Sort(visible));

        return items;
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

        _visibleNodes = visible;
        AppNames = visible.Select(n => n.Name).Order().ToList();
    }

    public IEnumerable<string> SearchAppNames(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return AppNames;
        var q = query.Trim();
        return _visibleNodes
               .Select(n => (n.Name, Score: MatchScore(n, q)))
               .Where(x => x.Score.match < int.MaxValue)
               .OrderBy(x => x.Score.match)
               .ThenBy(x => x.Score.cls)
               .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
               .Select(x => x.Name);
    }

    private static (int match, int cls) MatchScore(AppNode n, string q)
    {
        var cls = n.IsLegacy ? 2 : n.IsAdvanced ? 1 : 0;
        var match =
            n.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 :
            n.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 :
            n.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 :
            n.Category.Equals(q, StringComparison.OrdinalIgnoreCase) ? 3 :
            n.Category.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 4 :
            n.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ? 5 :
            n.SubCategory.Equals(q, StringComparison.OrdinalIgnoreCase) ? 6 :
            n.SubCategory.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 7 :
            n.SubCategory.Contains(q, StringComparison.OrdinalIgnoreCase) ? 8 :
            n.Description.Equals(q, StringComparison.OrdinalIgnoreCase) ? 9 :
            n.Description.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 10 :
            n.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ? 11 :
            n.IsAdvanced && "Advanced".Equals(q, StringComparison.OrdinalIgnoreCase) ? 12 :
            n.IsAdvanced && "Advanced".StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 13 :
            n.IsLegacy && "Legacy".Equals(q, StringComparison.OrdinalIgnoreCase) ? 14 :
            n.IsLegacy && "Legacy".StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 15 :
            int.MaxValue;
        return (match, cls);
    }

    public event Action? BeforeRebuildRows;
    public event Action? RowsFullyLoaded;

    public void BeginPresetUpdate()
    {
        _batchDepth++;
    }

    public void EndPresetUpdate()
    {
        if (_batchDepth == 0)
            return;
        _batchDepth--;
        if (_batchDepth > 0 || !_batchDirty)
            return;
        _batchDirty = false;
        RebuildRows();
    }

    private void RequestRebuild()
    {
        if (_batchDepth > 0)
        {
            _batchDirty = true;
            return;
        }

        RebuildRows();
    }

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

        const int firstBatch = 200;
        FlatRows.AddRange(rows[..Math.Min(firstBatch, rows.Count)]);

        if (rows.Count > firstBatch)
        {
            IsBuildingRows = true;
            _ = AddRemainingRowsAsync(rows, firstBatch, ct);
        }
        else
        {
            NotifyRowsFullyLoaded();
        }
    }

    private async Task AddRemainingRowsAsync(List<object> rows, int startIndex, CancellationToken ct)
    {
        const int batchSize = 40;
        for (var i = startIndex; i < rows.Count; i += batchSize)
        {
            if (ct.IsCancellationRequested)
            {
                IsBuildingRows = false;
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            if (ct.IsCancellationRequested)
            {
                IsBuildingRows = false;
                return;
            }

            FlatRows.AddRange(rows[i..Math.Min(i + batchSize, rows.Count)]);
        }

        IsBuildingRows = false;
        if (!ct.IsCancellationRequested)
        {
            // One extra yield so layout (higher priority) runs before signalling
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
            if (!ct.IsCancellationRequested)
                NotifyRowsFullyLoaded();
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
                                       : AppDeployService.GetInstallDir(n.SectionName)))
                   .ToList();

        await foreach (var (sectionName, bytes) in AppDiskUsageService.ScanAllAsync(apps))
        {
            cache.Sizes[sectionName] = bytes;
            foreach (var node in AllNodes.Where(n => string.Equals(n.SectionName, sectionName, StringComparison.OrdinalIgnoreCase)))
                await Dispatcher.UIThread.InvokeAsync(() => node.SetUsedBytes(bytes));
        }

        AppDiskUsageService.SaveCache(cache);
    }

    private void Notify([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
