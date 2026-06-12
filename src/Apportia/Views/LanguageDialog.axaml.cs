using Apportia.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class LanguageDialog : Window
{
    // Each entry: (raw INI key, formatted display name). "English" uses key "English" (base download).
    private readonly List<(string Key, string Display)> _entries = [];

    public LanguageDialog()
    {
        InitializeComponent();
    }

    public LanguageDialog(string appName, IReadOnlyList<string> languageKeys, string? preselect = null) : this()
    {
        AppLabel.Text = appName;

        _entries.Add(("English", "English"));
        _entries.AddRange(
            languageKeys
                .Select(k => (Key: k, Display: AppLanguageService.FormatLanguageName(k)))
                .OrderBy(e => e.Display));

        LanguageList.ItemsSource = _entries.Select(e => e.Display).ToList();

        var preselectDisplay = preselect is null or "English"
            ? "English"
            : AppLanguageService.FormatLanguageName(preselect);
        var idx = _entries.FindIndex(e => e.Display == preselectDisplay);
        LanguageList.SelectedIndex = idx >= 0 ? idx : 0;
    }

    public string? SelectedLanguageKey { get; private set; }

    private void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (LanguageList.SelectedIndex < 0 || LanguageList.SelectedIndex >= _entries.Count)
            return;
        SelectedLanguageKey = _entries[LanguageList.SelectedIndex].Key;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}