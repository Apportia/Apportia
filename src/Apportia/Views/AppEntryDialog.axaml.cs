using Apportia.Platform;
using Apportia.Services;
using Apportia.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Apportia.Views;

public sealed record EntryField(string Label, string Value);

public sealed record LanguageRow(string Language, string File, string Hash);

public partial class AppEntryDialog : Window
{
    public AppEntryDialog()
    {
        InitializeComponent();
    }

    public AppEntryDialog(AppNode node, IconManager iconManager) : this()
    {
        var updateDate = DateTime.TryParse(node.UpdateDate, out var dt)
            ? dt.ToString("dddd, d MMMM yyyy")
            : node.UpdateDate;
        Title = $"{node.Name} ({updateDate})";

        var installLocation = node.IsCustom
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
            new EntryField("Joined Date", node.JoinedDate),
            new EntryField("Requires Java", node.RequiresJava ? "Yes" : "")
        );

        VersionList.ItemsSource = Filter(
            new EntryField("Version", node.PackageVersion),
            new EntryField("Update Date", node.UpdateDate)
        );

        DownloadList.ItemsSource = Filter(
            new EntryField("Download File", node.DownloadFile),
            new EntryField("Hash", node.Hash),
            new EntryField("Download Path", node.DownloadPath),
            new EntryField("User Agent", node.UserAgent),
            new EntryField("Download Size", node.DownloadSize)
        );

        InstallList.ItemsSource = Filter(
            new EntryField("Install Size", node.InstallSize),
            new EntryField("Install Location", installLocation)
        );

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
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static EntryField[] Filter(params EntryField[] fields)
    {
        return fields.Where(f => !string.IsNullOrEmpty(f.Value)).ToArray();
    }
}
