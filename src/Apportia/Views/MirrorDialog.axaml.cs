using Apportia.Platform;
using Apportia.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class MirrorDialog : Window
{
    private readonly IReadOnlyList<(string Slug, string Label)> _mirrors = [];

    public MirrorDialog()
    {
        InitializeComponent();
    }

    public MirrorDialog(
        string appName,
        string? failedMirror,
        IReadOnlyList<(string Slug, string Label)> mirrors) : this()
    {
        _mirrors = mirrors;
        AppLabel.Text = appName;
        FailedMirrorText.Text = failedMirror != null
            ? string.Format(UiText.Dialog.MirrorFailedNamedFormat, failedMirror)
            : UiText.Dialog.MirrorFailedGeneric;

        MirrorList.ItemsSource = mirrors.Select(m => m.Label).ToList();

        if (mirrors.Count > 0)
            MirrorList.SelectedIndex = 0;
    }

    public string? SelectedMirror { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        var idx = MirrorList.SelectedIndex;
        if (idx >= 0 && idx < _mirrors.Count)
            SelectedMirror = _mirrors[idx].Slug;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}