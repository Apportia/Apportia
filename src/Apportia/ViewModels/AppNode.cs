using System.ComponentModel;
using Apportia.Models;
using Apportia.Services;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Apportia.ViewModels;

public sealed class AppNode : INotifyPropertyChanged
{
    private readonly IReadOnlyDictionary<string, (string File, string Hash)>? _languageVariants;
    private string _currentDate;
    private Bitmap _icon;
    private bool _isInstalled;
    private bool _isLaunching;

    public AppNode(
        AppEntry entry,
        Bitmap icon,
        ColumnWidths columns,
        bool isInstalled = false,
        bool isCustom = false,
        bool isAdvanced = false,
        bool isLegacy = false,
        bool isPlugin = false,
        string currentDate = "")
    {
        _isInstalled = isInstalled;
        _currentDate = currentDate;
        IsCustom = isCustom;
        IsAdvanced = isAdvanced;
        IsLegacy = isLegacy;
        IsPlugin = isPlugin;
        _icon = icon;
        Columns = columns;
        SectionName = entry.SectionName;
        Name = entry.Name;
        Description = entry.Description;
        Category = entry.Category;
        SubCategory = entry.SubCategory;
        RequiresJava = entry.RequiresJava;
        PackageVersion = entry.PackageVersion;
        DownloadSize = FormatMb(entry.DownloadSize);
        InstallSize = FormatMb(entry.InstallSize);
        DownloadSizeBytes = ParseMbToBytes(entry.DownloadSize);
        InstallSizeBytes = ParseMbToBytes(entry.InstallSize);
        DownloadFile = entry.DownloadFile;
        DownloadPath = entry.DownloadPath;
        Hash = entry.Hash;
        ReleaseDate = entry.ReleaseDate;
        UpdateDate = entry.UpdateDate;
        AppUrl = entry.AppUrl;
        _languageVariants = entry.LanguageVariants;
        columns.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ColumnWidths.HighlightInstalled):
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
                    break;
                case nameof(ColumnWidths.IsInstalling):
                    NotifyActionStates();
                    break;
            }
        };
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    public ColumnWidths Columns { get; }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled == value)
                return;
            _isInstalled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInstalled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNotInstalled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubCategory)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsUpdate)));
            NotifyActionStates();
        }
    }

    public bool IsQueued
    {
        get => ShowRemoveFromQueue;
        set
        {
            if (ShowRemoveFromQueue == value)
                return;
            ShowRemoveFromQueue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsQueued)));
            NotifyActionStates();
        }
    }

    public bool IsBeingInstalled
    {
        get => ShowCancelInstall;
        set
        {
            if (ShowCancelInstall == value)
                return;
            ShowCancelInstall = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBeingInstalled)));
            NotifyActionStates();
        }
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (_isLaunching == value)
                return;
            _isLaunching = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLaunching)));
        }
    }

    public bool IsNotInstalled => !_isInstalled;
    public bool IsAdvanced { get; }
    public bool IsLegacy { get; }
    public bool IsPlugin { get; }
    public bool RequiresJava { get; }
    public bool IsHighlighted => _isInstalled && Columns.HighlightInstalled;
    public bool HasUrl => !string.IsNullOrEmpty(AppUrl);
    public bool IsCustom { get; }
    public bool IsIndented { get; set; }

    public bool NeedsUpdate =>
        _isInstalled &&
        DateTime.TryParse(UpdateDate, out var ud) &&
        DateTime.TryParse(CurrentDate, out var cd) &&
        cd.Date < ud.Date;

    public bool ShowCancelInstall { get; private set; }

    public bool ShowAddToQueue => Columns.IsInstalling && !ShowCancelInstall && !ShowRemoveFromQueue && (!_isInstalled || NeedsUpdate);
    public bool ShowRemoveFromQueue { get; private set; }

    public bool ShowInstallActions => !Columns.IsInstalling && !ShowRemoveFromQueue && !ShowCancelInstall && !_isInstalled;
    public bool ShowUpdateActions => !Columns.IsInstalling && !ShowRemoveFromQueue && !ShowCancelInstall && NeedsUpdate;
    public bool ShowRunActions => _isInstalled && !ShowCancelInstall && !IsPlugin;
    public bool ShowUninstall => _isInstalled && !ShowCancelInstall;

    public string SectionName { get; }
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }

    public string SubCategory =>
        (IsAdvanced || IsLegacy) && !_isInstalled && !string.IsNullOrEmpty(field)
            ? $"{Category} \u2013 {field}"
            : field;

    public string PackageVersion { get; }
    public string DownloadSize { get; }
    public string InstallSize { get; }
    public long DownloadSizeBytes { get; }
    public long InstallSizeBytes { get; }
    public string DownloadFile { get; }
    public string DownloadPath { get; }
    public string Hash { get; }
    public string ReleaseDate { get; }
    public string UpdateDate { get; }

    public string CurrentDate
    {
        get => _currentDate;
        set
        {
            if (_currentDate == value)
                return;
            _currentDate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentDate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsUpdate)));
            NotifyActionStates();
        }
    }

    public string AppUrl { get; }

    public string UsedSize
    {
        get => string.IsNullOrEmpty(field) ? InstallSize : field;
        private set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsedSize)));
        }
    } = string.Empty;

    internal long UsedBytes { get; private set; }

    public bool HasLanguageVariants => _languageVariants?.Count > 0;

    public Bitmap Icon
    {
        get => _icon;
        set
        {
            if (_icon == value)
                return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool TryBeginLaunch()
    {
        if (_isLaunching)
            return false;
        IsLaunching = true;
        _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.UIThread.Post(() => IsLaunching = false));
        return true;
    }

    public void SetUsedBytes(long bytes)
    {
        UsedBytes = bytes;
        UsedSize = AppDiskUsageService.FormatBytes(bytes);
    }

    public IReadOnlyList<string>? GetLanguageKeys()
    {
        return _languageVariants?.Keys.ToList();
    }

    public bool TryGetLanguageVariant(string language, out string file, out string hash)
    {
        if (_languageVariants != null && _languageVariants.TryGetValue(language, out var v))
        {
            file = v.File;
            hash = v.Hash;
            return true;
        }

        file = hash = string.Empty;
        return false;
    }

    public bool HasLanguageVariantKey(string language)
    {
        return _languageVariants?.ContainsKey(language) ?? false;
    }

    private void NotifyActionStates()
    {
        var pc = PropertyChanged;
        if (pc == null)
            return;
        pc(this, new PropertyChangedEventArgs(nameof(ShowCancelInstall)));
        pc(this, new PropertyChangedEventArgs(nameof(ShowAddToQueue)));
        pc(this, new PropertyChangedEventArgs(nameof(ShowRemoveFromQueue)));
        pc(this, new PropertyChangedEventArgs(nameof(ShowInstallActions)));
        pc(this, new PropertyChangedEventArgs(nameof(ShowUpdateActions)));
        pc(this, new PropertyChangedEventArgs(nameof(ShowRunActions)));
        pc(this, new PropertyChangedEventArgs(nameof(ShowUninstall)));
    }

    private static long ParseMbToBytes(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return 0;
        var dash = raw.IndexOf('-');
        if (dash > 0 &&
            int.TryParse(raw.AsSpan(0, dash), out var lo) &&
            int.TryParse(raw.AsSpan(dash + 1), out var hi))
            return (long)Math.Max(lo, hi) * 1_048_576;
        return long.TryParse(raw, out var mb) ? mb * 1_048_576 : 0;
    }

    private static string FormatMb(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;
        var dash = raw.IndexOf('-');
        if (dash > 0 &&
            int.TryParse(raw.AsSpan(0, dash), out var lo) &&
            int.TryParse(raw.AsSpan(dash + 1), out var hi))
            return $"{Math.Max(lo, hi)} MB";
        return int.TryParse(raw, out var mb) ? $"{mb} MB" : raw;
    }
}
