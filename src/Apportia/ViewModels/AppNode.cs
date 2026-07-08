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

    public AppNode(
        AppEntry entry,
        Bitmap icon,
        ColumnWidths columns,
        bool isInstalled = false,
        bool isCustom = false,
        bool isPlugin = false,
        string currentDate = "")
    {
        Columns = columns;
        _icon = icon;
        IsInstalled = isInstalled;
        IsCustom = isCustom;
        IsPlugin = isPlugin;
        _currentDate = currentDate;
        SectionName = entry.SectionName;
        Name = entry.Name;
        Description = entry.Description;
        Category = entry.Category;
        SubCategory = entry.SubCategory;
        IsAdvanced = string.Equals(entry.Class, "Advanced", StringComparison.OrdinalIgnoreCase);
        IsLegacy = string.Equals(entry.Class, "Legacy", StringComparison.OrdinalIgnoreCase);
        RequiresJava = entry.RequiresJava;
        DisplayVersion = entry.DisplayVersion;
        PackageVersion = entry.PackageVersion;
        DownloadFile = entry.DownloadFile;
        DownloadPath = entry.DownloadPath;
        if (isCustom)
        {
            DownloadSize = "\u2014";
            InstallSize = "\u2014";
        }
        else
        {
            DownloadSizeMb = long.TryParse(entry.DownloadSize, out var dlMb) ? dlMb : 1;
            DownloadSize = AppDiskUsageService.FormatSize(DownloadSizeMb, true);
            InstallSizeMb = long.TryParse(entry.InstallSize, out var instMb) ? instMb : DownloadSizeMb;
            InstallSize = AppDiskUsageService.FormatSize(InstallSizeMb, true);
        }

        Hash = entry.Hash;
        JoinedDate = entry.JoinedDate;
        UpdateDate = entry.UpdateDate;
        Website = entry.Website;
        UserAgent = entry.UserAgent;
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

        IsRunning = RunningAppsService.IsRunning(SectionName);
        RunningAppsService.Changed += OnRunningChanged;
    }

    public bool IsRunning
    {
        get;
        private set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowTerminate)));
        }
    }

    public bool ShowTerminate => IsRunning && !IsPlugin;

    public ColumnWidths Columns { get; }

    public bool IsInstalled
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInstalled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNotInstalled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubCategory)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsUpdate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShownVersion)));
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

    public bool IsLaunchFx
    {
        get;
        private set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLaunchFx)));
        }
    }

    public bool IsSearchFx
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSearchFx)));
        }
    }

    public bool IsNotInstalled => !IsInstalled;
    public bool IsAdvanced { get; }
    public bool IsLegacy { get; }
    public bool IsPlugin { get; }
    public bool RequiresJava { get; }
    public bool IsHighlighted => IsInstalled && Columns.HighlightInstalled;
    public bool HasUrl => !string.IsNullOrEmpty(Website);
    public bool IsCustom { get; }
    public bool IsIndented { get; set; }

    public bool NeedsUpdate =>
        IsInstalled &&
        DateTime.TryParse(UpdateDate, out var ud) &&
        DateTime.TryParse(CurrentDate, out var cd) &&
        cd.Date < ud.Date;

    public bool ShowCancelInstall { get; private set; }

    public bool ShowAddToQueue => Columns.IsInstalling && !ShowCancelInstall && !ShowRemoveFromQueue && (!IsInstalled || NeedsUpdate);
    public bool ShowRemoveFromQueue { get; private set; }

    public bool ShowInstallActions => !Columns.IsInstalling && !ShowRemoveFromQueue && !ShowCancelInstall && !IsInstalled;
    public bool ShowUpdateActions => !Columns.IsInstalling && !ShowRemoveFromQueue && !ShowCancelInstall && NeedsUpdate;
    public bool ShowRunActions => IsInstalled && !ShowCancelInstall && !IsPlugin;
    public bool ShowVirusTotalActions => !IsPlugin && !ShowCancelInstall && (IsInstalled || !string.IsNullOrEmpty(Hash));
    public bool ShowPreview => !IsLegacy && !IsCustom && !IsPlugin;
    public bool ShowUninstall => IsInstalled && !ShowCancelInstall;

    public string SectionName { get; }
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }

    public string SubCategory =>
        (IsAdvanced || IsLegacy) && !IsInstalled && !string.IsNullOrEmpty(field)
            ? $"{Category} \u2013 {field}"
            : field;

    public string DisplayVersion { get; }
    public string PackageVersion { get; }

    public string? LocalDisplayVersion
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalDisplayVersion)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShownVersion)));
        }
    }

    public string? LocalPackageVersion
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalPackageVersion)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShownVersion)));
        }
    }

    public string ShownVersion => (IsInstalled ? LocalPackageVersion : null) ?? PackageVersion;
    public string DownloadSize { get; }
    public string InstallSize { get; }
    public long DownloadSizeMb { get; }
    public long InstallSizeMb { get; }
    public string DownloadFile { get; }
    public string DownloadPath { get; }
    public string Hash { get; }
    public string JoinedDate { get; }
    public string UpdateDate { get; }
    public string JoinedDateDisplay => RelativeDate(JoinedDate);
    public string UpdateDateDisplay => RelativeDate(UpdateDate);

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

    public string Website { get; }
    public string UserAgent { get; }

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

    private void OnRunningChanged(object? sender, string sectionName)
    {
        if (!string.Equals(sectionName, SectionName, StringComparison.OrdinalIgnoreCase))
            return;
        Dispatcher.UIThread.Post(() => IsRunning = RunningAppsService.IsRunning(SectionName));
    }

    public bool TryBeginLaunchFx()
    {
        if (IsLaunchFx)
            return false;
        IsLaunchFx = true;
        _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.UIThread.Post(() => IsLaunchFx = false));
        return true;
    }

    public void SetUsedBytes(long bytes)
    {
        UsedBytes = bytes;
        UsedSize = AppDiskUsageService.FormatSize(bytes);
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

    private static string RelativeDate(string raw)
    {
        if (!DateTime.TryParse(raw, out var date))
            return raw;
        var days = (DateTime.Today - date.Date).Days;
        if (days < 0)
            return date.ToString("MMM d, yyyy");
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            <= 6 => $"{days} days ago",
            7 => "1 week ago",
            _ => date.ToString("MMM d, yyyy")
        };
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
        pc(this, new PropertyChangedEventArgs(nameof(ShowVirusTotalActions)));
        pc(this, new PropertyChangedEventArgs(nameof(ShowUninstall)));
    }
}