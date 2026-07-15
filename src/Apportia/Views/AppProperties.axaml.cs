using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Apportia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Apportia.Views;

public sealed record EntryField(string Label, string Value);

public sealed record VersionField(string Label, string Installed, string Available)
{
    public bool HasUpdate => !string.IsNullOrEmpty(Available);
}

public sealed record LanguageRow(string Language, string File, string Hash);

public partial class AppProperties : Window
{
    private readonly string? _installLocation;
    private readonly AppNode? _node;
    private readonly string? _selectedLanguage;

    public AppProperties()
    {
        InitializeComponent();
        AddHandler(ContextRequestedEvent, OnContextRequested);
    }

    public AppProperties(AppNode node, AppImageManager iconManager) : this()
    {
        _node = node;

        Title = string.Format(UiText.Header.PropsTitleFormat, node.Name, RelativeDate.Format(node.UpdateDate));

        _installLocation = node.IsCustom
            ? Path.Combine(CustomAppService.CustomAppsDir, node.SectionName)
            : node.IsPlugin
                ? PluginService.GetInstallDir(node.SectionName)
                : AppDeployService.GetInstallDir(node.SectionName);

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
            new EntryField(UiText.Header.PropsSection, node.SectionName),
            new EntryField(UiText.Header.PropsName, node.Name),
            new EntryField(UiText.Header.PropsDescription, node.Description),
            new EntryField(UiText.Header.PropsWebsite, node.Website),
            new EntryField(UiText.Header.PropsCategory, node.Category),
            new EntryField(UiText.Header.PropsSubCategory, node.IsAdvanced || node.IsLegacy
                               ? node.SubCategory.Replace(prefix, string.Empty)
                               : node.SubCategory),
            new EntryField(UiText.Header.PropsClass, node.IsAdvanced ? UiText.Header.PropsClassAdvanced : node.IsLegacy ? UiText.Header.PropsClassLegacy : ""),
            new EntryField(UiText.Header.PropsJoinedDate, RelativeDate.Format(node.JoinedDate)),
            new EntryField(UiText.Header.PropsRequiresJava, node.RequiresJava ? UiText.Header.PropsYes : "")
        );

        var localDisplay = node.LocalDisplayVersion ?? node.DisplayVersion;
        var localPackage = node.LocalPackageVersion ?? node.PackageVersion;
        var availableDisplay = node.NeedsUpdate ? node.DisplayVersion : string.Empty;
        var availablePackage = node.NeedsUpdate ? node.PackageVersion : string.Empty;

        VersionList.ItemsSource = new VersionField[]
        {
            new(UiText.Header.PropsDisplayVersion, localDisplay, availableDisplay),
            new(UiText.Header.PropsPackageVersion, localPackage, availablePackage),
            new(UiText.Header.PropsUpdateDate, RelativeDate.Format(node.UpdateDate), string.Empty)
        };

        if (node.IsCustom)
        {
            DownloadSection.IsVisible = false;
            DownloadList.IsVisible = false;
        }
        else
        {
            DownloadList.ItemsSource = Filter(
                new EntryField(UiText.Header.PropsDownloadFile, node.DownloadFile),
                new EntryField(UiText.Header.PropsHash, node.Hash),
                new EntryField(UiText.Header.PropsDownloadPath, node.DownloadPath),
                new EntryField(UiText.Header.PropsUserAgent, node.UserAgent),
                new EntryField(UiText.Header.PropsDownloadSize, node.DownloadSize)
            );
        }

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
                            ? string.Format(UiText.Header.PropsInstalledFormat, node.UsedSize, AppDiskUsageService.FormatSize(node.UsedBytes - dataBytes), AppDiskUsageService.FormatSize(dataBytes))
                            : node.UsedSize;
                        Dispatcher.UIThread.Post(() => RefreshInstallList(usedSize));
                    }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void RefreshInstallList(string usedSizeValue)
    {
        InstallList.ItemsSource = Filter(
            new EntryField(UiText.Header.PropsInstallSize, _node?.InstallSize ?? ""),
            new EntryField(UiText.Header.PropsUsedSize, usedSizeValue),
            new EntryField(UiText.Header.PropsInstallLocation, _installLocation ?? ""),
            new EntryField(UiText.Header.PropsLanguage, _selectedLanguage ?? "")
        );
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not TextBlock tb || !tb.Classes.Contains("value") || string.IsNullOrEmpty(tb.Text))
            return;
        var text = tb.Text;
        var copyItem = new MenuItem { Header = UiText.Header.PropsCopy };
        copyItem.Click += async (_, _) =>
        {
            if (GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetValueAsync(DataFormat.Text, text);
        };
        var menu = new ContextMenu { Items = { copyItem } };
        e.Handled = true;
        menu.Open(tb);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static EntryField[] Filter(params EntryField[] fields)
    {
        return fields.Where(f => !string.IsNullOrEmpty(f.Value) && f.Value != "\u2014").ToArray();
    }
}