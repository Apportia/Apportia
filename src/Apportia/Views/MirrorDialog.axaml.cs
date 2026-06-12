using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class MirrorDialog : Window
{
    private readonly IReadOnlyList<(string Base, string Label)> _mirrors = [];

    public MirrorDialog()
    {
        InitializeComponent();
    }

    public MirrorDialog(
        string appName,
        string? failedMirror,
        IReadOnlyList<(string Base, string Label)> mirrors) : this()
    {
        _mirrors = mirrors;
        AppLabel.Text = appName;
        FailedMirrorText.Text = failedMirror != null
            ? $"The connection to {failedMirror} timed out.\n\nSelect a different mirror to retry the download:"
            : "The download connection timed out.\n\nSelect a mirror to retry:";

        MirrorList.ItemsSource = mirrors.Select(m => m.Label).ToList();

        if (mirrors.Count > 0)
            MirrorList.SelectedIndex = 0;
    }

    public string? SelectedMirror { get; private set; }

    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        var idx = MirrorList.SelectedIndex;
        if (idx >= 0 && idx < _mirrors.Count)
            SelectedMirror = _mirrors[idx].Base;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
