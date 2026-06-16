using Apportia.Platform;
using Apportia.Services;
using Apportia.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Apportia.Views;

public sealed record EntryField(string Label, string Value);

public sealed record LanguageRow(string Language, string File, string Hash);

public partial class AppProperties : Window
{
    private readonly string? _installLocation;
    private readonly AppNode? _node;
    private readonly string? _selectedLanguage;

    public AppProperties()
    {
        InitializeComponent();
    }

    public AppProperties(AppNode node, IconManager iconManager) : this()
    {
        _node = node;

        var updateDate = DateTime.TryParse(node.UpdateDate, out var dt)
            ? dt.ToString("dddd, MMMM d, yyyy")
            : node.UpdateDate;
        Title = $"{node.Name} ({updateDate})";

        _installLocation = node.IsCustom
            ? Path.Combine(CustomAppService.CustomAppsDir, node.SectionName)
            : node.IsPlugin
                ? PluginService.GetInstallDir(node.SectionName)
                : AppDownloadService.GetInstallDir(node.SectionName);

        var prefix = node.Category + " \u2013 ";

        var iconPath = node.IsCustom
            ? Path.Combine(CustomAppService.CustomAppImagesDir, node.SectionName + ".png")
            : iconManager.LocalPath(node.SectionName, 128);
        if (File.Exists(iconPath))
        {
            GeneralIcon.Source = new Bitmap(iconPath);
            GeneralIcon.IsVisible = true;
        }

        GeneralList.ItemsSource = Filter(
            new EntryField("Section", node.SectionName),
            new EntryField("Name", node.Name),
            new EntryField("Description", node.Description),
            new EntryField("Website", node.Website),
            new EntryField("Category", node.Category),
            new EntryField("Sub-Category", node.IsAdvanced || node.IsLegacy
                               ? node.SubCategory.Replace(prefix, string.Empty)
                               : node.SubCategory),
            new EntryField("Class", node.IsAdvanced ? "Advanced" : node.IsLegacy ? "Legacy" : ""),
            new EntryField("Joined Date", RelativeDate(node.JoinedDate)),
            new EntryField("Requires Java", node.RequiresJava ? "Yes" : "")
        );

        VersionList.ItemsSource = Filter(
            new EntryField("Display Version", node.DisplayVersion),
            new EntryField("Package Version", node.PackageVersion),
            new EntryField("Update Date", RelativeDate(node.UpdateDate))
        );

        DownloadList.ItemsSource = Filter(
            new EntryField("Download File", node.DownloadFile),
            new EntryField("Hash", node.Hash),
            new EntryField("Download Path", node.DownloadPath),
            new EntryField("User Agent", node.UserAgent),
            new EntryField("Download Size", node.DownloadSize)
        );

        _selectedLanguage = node is { HasLanguageVariants: true, IsInstalled: true }
            ? AppLanguageService.Load(node.SectionName) is { } savedLang
                ? AppLanguageService.FormatLanguageName(savedLang)
                : null
            : null;

        RefreshInstallList(node.UsedSize);

        var langKeys = node.GetLanguageKeys();
        if (!(langKeys?.Count > 0))
            return;
        LanguageList.ItemsSource = langKeys.Select(lang =>
        {
            node.TryGetLanguageVariant(lang, out var file, out var hash);
            return new LanguageRow(lang, file, hash);
        }).ToArray();
        LanguagesSection.IsVisible = true;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);

        if (_node is { IsInstalled: true } node && _installLocation is { } loc)
            _ = Task.Run(() => AppDiskUsageService.GetDirectorySize(Path.Combine(loc, "Data")))
                    .ContinueWith(t =>
                    {
                        var dataBytes = t.Result;
                        var usedSize = dataBytes > 0 && node.UsedBytes > 0
                            ? $"{node.UsedSize} (App: {AppDiskUsageService.FormatSize(node.UsedBytes - dataBytes)}, Data: {AppDiskUsageService.FormatSize(dataBytes)})"
                            : node.UsedSize;
                        Dispatcher.UIThread.Post(() => RefreshInstallList(usedSize));
                    }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void RefreshInstallList(string usedSizeValue)
    {
        InstallList.ItemsSource = Filter(
            new EntryField("Install Size", _node?.InstallSize ?? ""),
            new EntryField("Used Size", usedSizeValue),
            new EntryField("Install Location", _installLocation ?? ""),
            new EntryField("Language", _selectedLanguage ?? "")
        );
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string RelativeDate(string? raw)
    {
        if (!DateTime.TryParse(raw, out var date))
            return raw ?? string.Empty;
        var days = (DateTime.Today - date.Date).Days;
        var dayName = date.ToString("dddd");
        if (days < 0)
            return date.ToString("dddd, MMMM d, yyyy");
        return days switch
        {
            0 => $"{dayName}, Today",
            1 => $"{dayName}, Yesterday",
            <= 6 => $"{dayName}, {days} days ago",
            7 => $"{dayName}, 1 week ago",
            _ => date.ToString("dddd, MMMM d, yyyy")
        };
    }

    private static EntryField[] Filter(params EntryField[] fields)
    {
        return fields.Where(f => !string.IsNullOrEmpty(f.Value)).ToArray();
    }
}
