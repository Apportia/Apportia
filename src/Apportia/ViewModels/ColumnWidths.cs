using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Apportia.ViewModels;

public sealed class ColumnWidths : INotifyPropertyChanged
{
    private static readonly int[] IconSizes = [12, 16, 24, 32, 48, 64, 96, 128];
    private static readonly int[] FontSizes = [7, 9, 11, 13, 15];

    private int _fontSize = 13;
    private int _iconSize = 24;

    public string SortColumn { get; private set; } = "Name";
    public bool SortDescending { get; private set; }

    public int FontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize == value)
                return;
            _fontSize = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FontSizeSub)));
        }
    }

    public int FontSizeSub => _fontSize - 1;

    public int IconSize
    {
        get => _iconSize;
        set
        {
            if (_iconSize == value)
                return;
            _iconSize = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconLoadSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileWidth)));
        }
    }

    public int IconLoadSize => _iconSize < 16 ? 16 : _iconSize;

    public bool IsGridView
    {
        get;
        set => Set(ref field, value);
    }

    public double TileWidth => Math.Max(80, _iconSize + 56);

    public double Name
    {
        get;
        set => Set(ref field, Clamp(value, 80, 500));
    } = 200;

    public double Version
    {
        get;
        set => Set(ref field, Clamp(value, 60, 200));
    } = 90;

    public double Download
    {
        get;
        set => Set(ref field, Clamp(value, 60, 180));
    } = 85;

    public double Install
    {
        get;
        set => Set(ref field, Clamp(value, 60, 180));
    } = 80;

    public double Current
    {
        get;
        set => Set(ref field, Clamp(value, 70, 160));
    } = 90;

    public double Joined
    {
        get;
        set => Set(ref field, Clamp(value, 70, 160));
    } = 90;

    public double Updated
    {
        get;
        set => Set(ref field, Clamp(value, 70, 160));
    } = 90;

    public bool ShowMetaColumns
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool ShowCurrentColumn
    {
        get;
        set => Set(ref field, value);
    }

    public bool ShowJoinedColumn
    {
        get;
        set => Set(ref field, value);
    } = true;

    public bool ShowUsedColumn
    {
        get;
        set => Set(ref field, value);
    }

    public double Used
    {
        get;
        set => Set(ref field, Clamp(value, 60, 180));
    } = 75;

    public bool IsInstalling
    {
        get;
        set => Set(ref field, value);
    }

    public bool HighlightInstalled
    {
        get;
        set => Set(ref field, value);
    } = true;

    public string NameHeader => Header("Name");
    public string VersionHeader => Header("Version");
    public string DownloadHeader => Header("Download");
    public string InstallHeader => Header("Install");
    public string CurrentHeader => "Current";
    public string JoinedHeader => Header("Joined");
    public string UpdatedHeader => Header("Updated");
    public string UsedHeader => Header("Used");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CycleFontSize(bool reverse = false)
    {
        var idx = Array.IndexOf(FontSizes, _fontSize);
        FontSize = FontSizes[(idx + (reverse ? -1 : 1) + FontSizes.Length) % FontSizes.Length];
    }

    public void CycleIconSize(bool reverse = false)
    {
        var idx = Array.IndexOf(IconSizes, _iconSize);
        IconSize = IconSizes[(idx + (reverse ? -1 : 1) + IconSizes.Length) % IconSizes.Length];
    }

    public void SetSort(string column, bool descending)
    {
        SortColumn = column;
        SortDescending = descending;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SortColumn)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SortDescending)));
        foreach (var h in new[]
        {
            nameof(NameHeader), nameof(VersionHeader), nameof(DownloadHeader),
            nameof(InstallHeader), nameof(JoinedHeader), nameof(UpdatedHeader),
            nameof(UsedHeader)
        })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(h));
    }

    private string Header(string col)
    {
        if (SortColumn != col)
            return col;
        return col + (SortDescending ? " \u25BC" : " \u25B2");
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
