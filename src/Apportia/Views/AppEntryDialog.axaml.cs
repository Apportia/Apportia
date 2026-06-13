using Apportia.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public sealed record EntryField(string Label, string Value);

public sealed record LanguageRow(string Language, string File, string Hash);

public partial class AppEntryDialog : Window
{
    public AppEntryDialog()
    {
        InitializeComponent();
    }

    public AppEntryDialog(AppNode node) : this()
    {
        Title = node.Name;

        GeneralList.ItemsSource = Filter(
            new EntryField("Name", node.Name),
            new EntryField("Section", node.SectionName),
            new EntryField("Description", node.Description),
            new EntryField("Category", node.Category),
            new EntryField("Subcategory", node.SubCategory),
            new EntryField("Website", node.Website),
            new EntryField("Joined Date", node.JoinedDate),
            new EntryField("Class", node.IsAdvanced ? "Advanced" : node.IsLegacy ? "Legacy" : ""),
            new EntryField("Requires Java", node.RequiresJava ? "Yes" : "")
        );

        VersionList.ItemsSource = Filter(
            new EntryField("Version", node.PackageVersion),
            new EntryField("Update Date", node.UpdateDate)
        );

        DownloadList.ItemsSource = Filter(
            new EntryField("Download File", node.DownloadFile),
            new EntryField("Download Path", node.DownloadPath),
            new EntryField("Download Size", node.DownloadSize),
            new EntryField("Install Size", node.InstallSize),
            new EntryField("Hash", node.Hash),
            new EntryField("User Agent", node.UserAgent)
        );

        var langKeys = node.GetLanguageKeys();
        if (!(langKeys?.Count > 0))
            return;
        LanguageList.ItemsSource = langKeys
                                   .Select(lang =>
                                   {
                                       node.TryGetLanguageVariant(lang, out var file, out var hash);
                                       return new LanguageRow(lang, file, hash);
                                   })
                                   .ToArray();
        LanguagesSection.IsVisible = true;
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