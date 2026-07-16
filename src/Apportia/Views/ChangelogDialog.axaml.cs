using Apportia.Platform;
using Apportia.Text;
using Apportia.Ui;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Apportia.Views;

public partial class ChangelogDialog : Window
{
    private readonly string? _changelog;
    private readonly Version? _version;

    public ChangelogDialog()
    {
        InitializeComponent();
    }

    public ChangelogDialog(Version version, string? changelog) : this()
    {
        _version = version;
        _changelog = changelog;
    }

    public bool Confirmed { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);

        VersionText.Text = string.Format(UiText.Dialog.ChangelogVersionFormat, _version);

        var textBrush = this.FindResource("AppTextBrush") as IBrush ?? Brushes.White;
        var content = _changelog != null ? ExtractChangelog(_changelog) : string.Empty;
        ChangelogPanel.Children.Add(MarkdownRenderer.Render(content, textBrush));
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnInstall(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private static string ExtractChangelog(string body)
    {
        var detailsStart = body.IndexOf("<details>", StringComparison.OrdinalIgnoreCase);
        var detailsEnd = body.IndexOf("</details>", StringComparison.OrdinalIgnoreCase);
        if (detailsStart < 0 || detailsEnd <= detailsStart)
            return body.Trim();

        var inner = body[(detailsStart + "<details>".Length)..detailsEnd];
        var sumEnd = inner.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase);
        if (sumEnd >= 0)
            inner = inner[(sumEnd + "</summary>".Length)..];
        return inner.Trim();
    }
}