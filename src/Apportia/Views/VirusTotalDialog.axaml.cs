using System.Collections.ObjectModel;
using System.Diagnostics;
using Apportia.Models;
using Apportia.Platform;
using Apportia.Services;
using Apportia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Apportia.Views;

public sealed record VtFileEntry(string RelativePath, string? Sha256, VtFileStatus Status)
{
    public string DisplayText => Status switch
    {
        VtFileStatus.Fresh => $"{RelativePath}  \u2713",
        VtFileStatus.Stale => $"{RelativePath}  \u21ba",
        _ => RelativePath
    };
}

public sealed record EntryRow(string Label, string? Value);

public sealed record VtEngineRow(string Engine, string? Result, string Category)
{
    public bool IsMalicious => Category == "malicious";
    public bool IsSuspicious => Category == "suspicious";
    public bool IsHarmlessOrUndetected => Category is "harmless" or "undetected";
    public bool IsFailure => Category == "failure";
    public bool IsTimeout => Category == "timeout";
    public bool IsConfirmedTimeout => Category == "confirmed-timeout";
    public bool HasResult => !string.IsNullOrEmpty(Result);
    public string DisplayResult => string.IsNullOrEmpty(Result) ? "\u2014" : Result;

    public string DisplayCategory => Category switch
    {
        "malicious" => "\u2718 malicious",
        "suspicious" => "\u26a0 suspicious",
        "confirmed-timeout" => "\u25cb confirmed-timeout",
        "undetected" => "\u2713 undetected",
        "harmless" => "\u2713 harmless",
        "failure" => "\u2715 failure",
        "timeout" => "\u25cb timeout",
        "type-unsupported" => "\u2298 type-unsupported",
        _ => Category
    };
}

public sealed record VtSandboxRow(string Sandbox, string Category, string Classification)
{
    public bool IsMalicious => Category == "malicious";
    public bool IsSuspicious => Category == "suspicious";
    public bool IsHarmlessOrUndetected => Category is "harmless" or "undetected";
    public bool IsFailure => Category == "failure";
    public bool IsTimeout => Category == "timeout";
    public bool IsConfirmedTimeout => Category == "confirmed-timeout";
}

public sealed record VtTridRow(string FileType, string Probability);

public partial class VirusTotalDialog : Window
{
    private readonly string _appDir = string.Empty;
    private readonly ObservableCollection<VtFileEntry> _entries = [];
    private readonly AppNode? _node;
    private readonly Dictionary<string, string> _sessionHashes = new();
    private readonly VtStore _store = new();
    private GridLength _engineHeaderEngineColWidth;
    private string? _sha256PendingUpload;

    public VirusTotalDialog()
    {
        InitializeComponent();
    }

    public VirusTotalDialog(AppNode node) : this()
    {
        _node = node;

        Title = $"VirusTotal \u2014 {node.Name}";

        _store = VirusTotalService.LoadStore();
        ApiKeyBox.Text = _store.ApiKey;

        FileCombo.ItemsSource = _entries;

        if (!node.IsInstalled)
        {
            PopulateDownloadEntries();
            return;
        }

        _appDir = node.IsCustom
            ? Path.Combine(CustomAppService.CustomAppsDir, node.SectionName)
            : node.IsPlugin
                ? PluginService.GetInstallDir(node.SectionName)
                : AppDownloadService.GetInstallDir(node.SectionName);

        foreach (var rel in VirusTotalService.GetTopLevelBinaries(_appDir))
            _entries.Add(BuildEntry(rel));

        _ = LoadSubdirBinariesAsync();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
        _engineHeaderEngineColWidth = EngineHeaderGrid.ColumnDefinitions[0].Width;

        ApiKeyBox.TextChanged += (_, _) => UpdateScanButtonState();

        if (_entries.Count > 0)
            FileCombo.SelectedIndex = 0;
        else if (_node?.IsInstalled == false)
            SetStatus("No download file information available for this app.", true);
        else
            SetStatus("No binary files found in the app directory.", true);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnOpenWebsite(object? sender, RoutedEventArgs e)
    {
        var url = "https://www.virustotal.com/";
        if (FileCombo.SelectedItem is VtFileEntry entry)
        {
            var sha256 = entry.Sha256 ?? _sessionHashes.GetValueOrDefault(entry.RelativePath);
            if (sha256 != null)
                url = $"https://www.virustotal.com/gui/file/{sha256}";
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void OnSaveApiKey(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HandleSaveApiKeyAsync();
        }
        catch (Exception ex)
        {
            await Alert("Unexpected Error", ex.ToString());
        }
    }

    private async void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            await HandleFileSelectionChangedAsync();
        }
        catch (Exception ex)
        {
            await Alert("Unexpected Error", ex.ToString());
        }
    }

    private async Task HandleFileSelectionChangedAsync()
    {
        if (FileCombo.SelectedItem is not VtFileEntry entry) return;

        _sha256PendingUpload = null;
        ScanButton.Content = "Scan";
        ClearResults();
        ScanButton.IsEnabled = false;
        StatusBorder.IsVisible = false;

        var sha256 = await ResolveSha256Async(entry);
        if (sha256 == null)
        {
            ResizeWindow(MinWidth, MinHeight);
            return;
        }

        var cached = VirusTotalService.LoadCachedResult(sha256);
        if (cached?.Data?.Attributes != null)
        {
            var isStale = entry.Sha256 != null
                          && VirusTotalService.GetCacheStatus(entry.Sha256, _node?.UpdateDate ?? string.Empty) == VtFileStatus.Stale;
            DisplayResult(cached, sha256, isStale);
            ScanButton.Content = "Rescan";
        }
        else
        {
            ResizeWindow(MinWidth, MinHeight);
            ScanButton.Content = "Scan";
            SetStatus("Not yet scanned. Click Scan to query VirusTotal.", false);
        }

        UpdateScanButtonState();
    }

    private async void OnScan(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HandleScanAsync();
        }
        catch (Exception ex)
        {
            await Alert("Unexpected Error", ex.ToString());
            UpdateScanButtonState();
        }
    }

    private async Task HandleScanAsync()
    {
        if (FileCombo.SelectedItem is not VtFileEntry entry)
        {
            await Alert("Scan Error", "No file selected.");
            return;
        }

        var apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
        {
            await Alert("Scan Error", "Please enter an API key.");
            return;
        }

        var sha256 = await ResolveSha256Async(entry);
        if (sha256 == null) return;

        if (_sha256PendingUpload != null)
        {
            await HandleUploadAndScanAsync(entry, sha256, apiKey);
            return;
        }

        ScanButton.IsEnabled = false;
        SetStatus("Querying VirusTotal...", false);
        ClearResults();

        var (response, error, notFound) = await VirusTotalService.QueryAsync(sha256, apiKey);

        if (notFound)
        {
            ResizeWindow(MinWidth, MinHeight);
            if (_node?.IsInstalled == true)
            {
                _sha256PendingUpload = sha256;
                ScanButton.Content = "Upload & Scan";
                SetStatus("File not found in VirusTotal database. Click 'Upload & Scan' to submit it.", false);
            }
            else
            {
                SetStatus("This file has not been submitted to VirusTotal yet.", false);
            }

            UpdateScanButtonState();
            return;
        }

        if (error != null)
        {
            UpdateScanButtonState();
            await Alert("VirusTotal Error", error);
            return;
        }

        if (response?.Data?.Attributes == null)
        {
            UpdateScanButtonState();
            await Alert("VirusTotal Error", "No results returned.");
            return;
        }

        DisplayResult(response, sha256, false);
        RefreshCurrentEntryAsFresh(sha256);
        ScanButton.Content = "Rescan";
        UpdateScanButtonState();
        StatusBorder.IsVisible = false;
    }

    private async Task HandleUploadAndScanAsync(VtFileEntry entry, string sha256, string apiKey)
    {
        ScanButton.IsEnabled = false;
        ClearResults();
        SetStatus($"Uploading \u2018{entry.RelativePath}\u2019 to VirusTotal...", false);

        var filePath = Path.Combine(_appDir, entry.RelativePath);
        var (analysisId, uploadError) = await VirusTotalService.UploadAsync(filePath, apiKey);
        if (uploadError != null)
        {
            UpdateScanButtonState();
            await Alert("Upload Error", uploadError);
            return;
        }

        if (analysisId == null)
        {
            UpdateScanButtonState();
            await Alert("Upload Error", "No analysis ID returned.");
            return;
        }

        var uploadedAt = DateTime.Now;
        var analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        analysisTimer.Tick += (_, _) =>
        {
            var elapsed = (int)(DateTime.Now - uploadedAt).TotalSeconds;
            SetStatus($"File uploaded. VirusTotal is analyzing across all engines... ({elapsed}s elapsed)", false);
        };
        analysisTimer.Start();

        var (resultSha256, pollError) = await VirusTotalService.PollAnalysisAsync(analysisId, apiKey);
        analysisTimer.Stop();

        if (pollError != null)
        {
            UpdateScanButtonState();
            await Alert("Analysis Error", pollError);
            return;
        }

        var querySha256 = resultSha256 ?? sha256;
        SetStatus("Analysis complete. Fetching full report...", false);
        var (response, queryError, _) = await VirusTotalService.QueryAsync(querySha256, apiKey);
        if (queryError != null)
        {
            UpdateScanButtonState();
            await Alert("VirusTotal Error", queryError);
            return;
        }

        if (response?.Data?.Attributes == null)
        {
            UpdateScanButtonState();
            await Alert("VirusTotal Error", "No results returned after analysis.");
            return;
        }

        _sha256PendingUpload = null;
        DisplayResult(response, querySha256, false);
        RefreshCurrentEntryAsFresh(querySha256);
        ScanButton.Content = "Rescan";
        UpdateScanButtonState();
        StatusBorder.IsVisible = false;
    }

    private async Task<string?> ResolveSha256Async(VtFileEntry entry)
    {
        if (entry.Sha256 != null)
            return entry.Sha256;

        if (_sessionHashes.TryGetValue(entry.RelativePath, out var cached))
            return cached;

        var filePath = Path.Combine(_appDir, entry.RelativePath);
        SetStatus("Computing file hash...", false);
        try
        {
            var sha256 = await Task.Run(() => VirusTotalService.ComputeSha256(filePath));
            _sessionHashes[entry.RelativePath] = sha256;

            if (_node != null)
            {
                if (!_store.Files.TryGetValue(_node.SectionName, out var fileMap))
                    _store.Files[_node.SectionName] = fileMap = new Dictionary<string, string>();
                fileMap[entry.RelativePath] = sha256;
                VirusTotalService.SaveStore(_store);
            }

            StatusBorder.IsVisible = false;
            return sha256;
        }
        catch (Exception ex)
        {
            await Alert("Hash Error", $"Failed to read '{entry.RelativePath}':\n{ex.Message}");
            return null;
        }
    }

    private void DisplayResult(VtResponse response, string sha256, bool isStale)
    {
        var attrs = response.Data!.Attributes!;
        var stats = attrs.LastAnalysisStats;

        if (stats != null)
        {
            var total = stats.Malicious + stats.Suspicious + stats.Undetected + stats.Harmless + stats.Timeout;
            var detected = stats.Malicious + stats.Suspicious;
            DetectionBadge.Text = $"{detected}/{total}";
            var badgeColor =
                stats.Malicious > 0
                    ? "#CC2222"
                    : stats.Suspicious > 0
                        ? "#CC8800"
                        : stats.ConfirmedTimeout > 0
                            ? "#CCCC00"
                            : "#22AA44";
            DetectionBadge.Foreground = new SolidColorBrush(Color.Parse(badgeColor));
            DetectionLabel.Text = detected == 0 ? "no threats detected" : "engines detected a threat";
        }

        if (attrs.LastAnalysisDate > 0)
        {
            var scanDate = DateTimeOffset.FromUnixTimeSeconds(attrs.LastAnalysisDate).LocalDateTime;
            ScanDateText.Text = isStale
                ? $"Cached {scanDate:yyyy-MM-dd} (outdated)"
                : $"Scanned {scanDate:yyyy-MM-dd}";
        }

        FileInfoList.ItemsSource = Filter([
            new EntryRow("Name", attrs.MeaningfulName),
            new EntryRow("Type", attrs.TypeDescription),
            new EntryRow("Magic", attrs.Magic),
            new EntryRow("Size", attrs.Size > 0 ? AppDiskUsageService.FormatSize(attrs.Size) : null),
            new EntryRow("Tags", attrs.Tags is { Count: > 0 } ? string.Join(", ", attrs.Tags) : null),
            new EntryRow("Type Tags", attrs.TypeTags is { Count: > 0 } ? string.Join(", ", attrs.TypeTags) : null),
            new EntryRow("Known As", attrs.Names is { Count: > 0 } ? string.Join(", ", attrs.Names) : null)
        ]);

        HashList.ItemsSource = Filter([
            new EntryRow("SHA256", sha256),
            new EntryRow("SHA1", attrs.Sha1),
            new EntryRow("MD5", attrs.Md5)
        ]);

        SubmissionList.ItemsSource = Filter([
            new EntryRow("Created", attrs.CreationDate > 0 ? FormatUnixDate(attrs.CreationDate) : null),
            new EntryRow("First Seen", attrs.FirstSubmissionDate > 0 ? FormatUnixDate(attrs.FirstSubmissionDate) : null),
            new EntryRow("Last Seen", attrs.LastSubmissionDate > 0 ? FormatUnixDate(attrs.LastSubmissionDate) : null),
            new EntryRow("Submissions", attrs.TimesSubmitted.ToString()),
            new EntryRow("Sources", attrs.UniqueSources.ToString())
        ]);

        CommunityList.ItemsSource = Filter([
            new EntryRow("Reputation", attrs.Reputation.ToString()),
            new EntryRow("Votes Harmless", attrs.TotalVotes?.Harmless.ToString()),
            new EntryRow("Votes Malicious", attrs.TotalVotes?.Malicious.ToString())
        ]);

        var sig = attrs.SignatureInfo;
        if (sig != null)
        {
            SignatureList.ItemsSource = Filter([
                new EntryRow("Product", sig.Product),
                new EntryRow("Description", sig.Description),
                new EntryRow("Version", sig.FileVersion),
                new EntryRow("Internal Name", sig.InternalName),
                new EntryRow("Original Name", sig.OriginalName),
                new EntryRow("Copyright", sig.Copyright),
                new EntryRow("Signers", sig.Signers),
                new EntryRow("Signing Date", sig.SigningDate),
                new EntryRow("Verified", sig.Verified)
            ]);
            SignatureSection.IsVisible = true;
        }

        if (attrs.SandboxVerdicts is { Count: > 0 })
        {
            SandboxList.ItemsSource = attrs.SandboxVerdicts
                                           .Select(kv => new VtSandboxRow(
                                                       kv.Value.SandboxName ?? kv.Key,
                                                       kv.Value.Category ?? string.Empty,
                                                       kv.Value.MalwareClassification is { Count: > 0 }
                                                           ? string.Join(", ", kv.Value.MalwareClassification)
                                                           : string.Empty))
                                           .OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                                           .ThenBy(r => r.Sandbox, StringComparer.OrdinalIgnoreCase)
                                           .ToArray();
            SandboxSection.IsVisible = true;
        }

        if (attrs.Trid is { Count: > 0 })
        {
            TridList.ItemsSource = attrs.Trid
                                        .Where(t => !string.IsNullOrEmpty(t.FileType))
                                        .Select(t => new VtTridRow(t.FileType!, $"{t.Probability:0.0}%"))
                                        .ToArray();
            TridSection.IsVisible = true;
        }

        if (attrs.LastAnalysisResults != null)
        {
            var engineRows = attrs.LastAnalysisResults.Values
                                  .Select(r => new VtEngineRow(r.EngineName ?? "Unknown", r.Result, r.Category ?? "unknown"))
                                  .OrderBy(r => r.Category switch
                                  {
                                      "malicious" => 0,
                                      "suspicious" => 1,
                                      "confirmed-timeout" => 2,
                                      "undetected" => 3,
                                      "harmless" => 4,
                                      "failure" => 5,
                                      "timeout" => 6,
                                      "type-unsupported" => 7,
                                      _ => 8
                                  })
                                  .ThenBy(r => r.Engine, StringComparer.OrdinalIgnoreCase)
                                  .ToArray();
            ApplyEngineResultLayout(engineRows);
        }

        ResizeWindow(860, 700);
        ResultsPanel.IsVisible = true;
    }

    private async Task LoadSubdirBinariesAsync()
    {
        try
        {
            var subdirFiles = await Task.Run(() => VirusTotalService.GetSubdirBinaries(_appDir));
            var sorted = _entries.Select(e => e.RelativePath)
                                 .Concat(subdirFiles)
                                 .OrderBy(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                 .ThenBy(p => p.Count(c => c == Path.DirectorySeparatorChar || c == '/'))
                                 .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                                 .Select(BuildEntry)
                                 .ToList();
            _entries.Clear();
            foreach (var entry in sorted)
                _entries.Add(entry);
            if (_entries.Count > 0)
                FileCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            await Alert("Directory Scan Error", $"Failed to scan subdirectories:\n{ex.Message}");
        }
    }

    private void PopulateDownloadEntries()
    {
        if (_node == null || string.IsNullOrEmpty(_node.DownloadFile) || string.IsNullOrEmpty(_node.Hash))
            return;
        _entries.Add(BuildDownloadEntry(_node.DownloadFile, _node.Hash));
        var languages = _node.GetLanguageKeys();
        if (languages == null)
            return;
        foreach (var lang in languages)
        {
            if (_node.TryGetLanguageVariant(lang, out var file, out var hash))
                _entries.Add(BuildDownloadEntry($"{file}  [{lang}]", hash));
        }
    }

    private VtFileEntry BuildDownloadEntry(string displayName, string hash)
    {
        var status = VirusTotalService.GetCacheStatus(hash, _node?.UpdateDate ?? string.Empty);
        return new VtFileEntry(displayName, hash, status);
    }

    private void RefreshCurrentEntryAsFresh(string sha256)
    {
        var idx = FileCombo.SelectedIndex;
        if (idx < 0 || idx >= _entries.Count) return;
        var old = _entries[idx];
        _entries[idx] = new VtFileEntry(old.RelativePath, sha256, VtFileStatus.Fresh);
        FileCombo.SelectedIndex = idx;
    }

    private VtFileEntry BuildEntry(string relativePath)
    {
        string? sha256 = null;
        if (_node != null && _store.Files.TryGetValue(_node.SectionName, out var fileMap))
            fileMap.TryGetValue(relativePath, out sha256);
        var status = sha256 != null && _node != null
            ? VirusTotalService.GetCacheStatus(sha256, _node.UpdateDate)
            : VtFileStatus.Unknown;
        return new VtFileEntry(relativePath, sha256, status);
    }

    private async Task HandleSaveApiKeyAsync()
    {
        var key = ApiKeyBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            await Alert("Save API Key", "No API key entered.");
            return;
        }

        var dlg = new AppDialog(
            "Save API Key",
            $"How should the API key be saved?\n\n" +
            $"Permanent storage writes the key unencrypted to:\n{VirusTotalService.IndexPath}",
            "Session Only",
            "Save Permanently") { Icon = Icon };
        await dlg.ShowDialog(this);

        switch (dlg.Result)
        {
            case "Save Permanently":
                _store.ApiKey = key;
                VirusTotalService.SaveStore(_store);
                break;
            case "Session Only" when _store.ApiKey != null:
                _store.ApiKey = null;
                VirusTotalService.SaveStore(_store);
                break;
        }
    }

    private void UpdateScanButtonState()
    {
        ScanButton.IsEnabled = FileCombo.SelectedItem is VtFileEntry
                               && !string.IsNullOrWhiteSpace(ApiKeyBox.Text?.Trim());
    }

    private void ResizeWindow(double width, double height)
    {
        if (Math.Abs(Width - width) < 1 && Math.Abs(Height - height) < 1)
            return;

        var deltaX = (int)((width - Width) / 2);
        var deltaY = (int)((height - Height) / 2);

        Width = width;
        Height = height;

        var posX = Position.X - deltaX;
        var posY = Position.Y - deltaY;

        var screen = Screens.ScreenFromWindow(this);
        if (screen == null)
            return;

        var wa = screen.WorkingArea;
        var scale = RenderScaling;
        var physW = (int)(width * scale);
        var physH = (int)(height * scale);
        var x = Math.Clamp(posX, wa.X, wa.X + wa.Width - physW);
        var y = Math.Clamp(posY, wa.Y, wa.Y + wa.Height - physH);
        var target = new PixelPoint(x, y);

        Dispatcher.UIThread.Post(() => Position = target, DispatcherPriority.Background);
    }

    private void ClearResults()
    {
        ResultsPanel.IsVisible = false;
        FileInfoList.ItemsSource = null;
        HashList.ItemsSource = null;
        SubmissionList.ItemsSource = null;
        CommunityList.ItemsSource = null;
        SignatureList.ItemsSource = null;
        SignatureSection.IsVisible = false;
        SandboxList.ItemsSource = null;
        SandboxSection.IsVisible = false;
        TridList.ItemsSource = null;
        TridSection.IsVisible = false;
        EngineList.ItemsSource = null;
        EngineList.ItemTemplate = Resources["EngineRowTemplate"] as IDataTemplate;
        EngineResultHeader.IsVisible = true;
        EngineHeaderGrid.ColumnDefinitions[0].Width = _engineHeaderEngineColWidth;
        EngineHeaderGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
    }

    private void ApplyEngineResultLayout(VtEngineRow[] rows)
    {
        var hasResult = rows.Any(r => r.HasResult);
        EngineResultHeader.IsVisible = hasResult;
        EngineHeaderGrid.ColumnDefinitions[0].Width = hasResult
            ? _engineHeaderEngineColWidth
            : new GridLength(1, GridUnitType.Star);
        EngineHeaderGrid.ColumnDefinitions[1].Width = hasResult
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        EngineList.ItemTemplate = Resources[hasResult ? "EngineRowTemplate" : "EngineRowTemplateNoResult"] as IDataTemplate;
        EngineList.ItemsSource = rows;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? new SolidColorBrush(Color.Parse("#CC2222"))
            : Application.Current?.FindResource("AppTextBrush") as IBrush ?? Brushes.Gray;
        StatusBorder.IsVisible = true;
    }

    private async Task Alert(string title, string message)
    {
        var dlg = new AppDialog(title, message, "Copy", "OK") { Icon = Icon };
        await dlg.ShowDialog(this);
        if (dlg.Result == "Copy")
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetValueAsync(DataFormat.Text, message);
        }
    }

    private static string FormatUnixDate(long unixSeconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    private static EntryRow[] Filter(EntryRow[] rows)
    {
        return rows.Where(r => !string.IsNullOrEmpty(r.Value)).ToArray();
    }
}
