using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class LanguageDialog : Window
{
    private readonly List<(string Key, string Display)> _entries = [];

    public LanguageDialog()
    {
        InitializeComponent();
    }

    public LanguageDialog(string appName, IReadOnlyList<string> languageKeys, string? preselect = null) : this()
    {
        PromptLine1 = string.Format(UiText.Dialog.LanguagePromptLine1Format, appName);

        _entries.Add((UiText.Dialog.LanguageEnglish, UiText.Dialog.LanguageEnglish));
        _entries.AddRange(
            languageKeys
                .Select(k => (Key: k, Display: AppLanguageService.FormatLanguageName(k)))
                .OrderBy(e => e.Display));

        LanguageList.ItemsSource = _entries.Select(e => e.Display).ToList();

        var preselectDisplay = preselect is null or UiText.Dialog.LanguageEnglish
            ? UiText.Dialog.LanguageEnglish
            : AppLanguageService.FormatLanguageName(preselect);
        var idx = _entries.FindIndex(e => e.Display == preselectDisplay);
        LanguageList.SelectedIndex = idx >= 0 ? idx : 0;
    }

    public string PromptLine1 { get; } = string.Empty;

    public string? SelectedLanguageKey { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

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
