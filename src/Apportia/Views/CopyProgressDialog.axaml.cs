using Apportia.Models;
using Apportia.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Path = System.IO.Path;

namespace Apportia.Views;

public partial class CopyProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _closeConfirmed;
    private bool _copyDone;

    public CopyProgressDialog()
    {
        InitializeComponent();
    }

    public CopyProgressDialog(string sourceFolder) : this()
    {
        FolderLabel.Text = sourceFolder;
    }

    public CancellationToken CancellationToken => _cts.Token;

    public void Report(CopyProgress progress)
    {
        if (progress.Total > 0)
        {
            CopyProgressBar.IsIndeterminate = false;
            CopyProgressBar.Value = (double)progress.Copied / progress.Total * 100;
            StatusText.Text = $"{progress.Copied} of {progress.Total} files copied";
        }

        if (!string.IsNullOrEmpty(progress.File))
            AddFileRow(progress.File);
    }

    public async void NotifyDone()
    {
        try
        {
            _copyDone = true;
            CopyProgressBar.IsIndeterminate = false;
            CopyProgressBar.Value = 100;
            StatusText.Text = "All files copied.";
            await Task.Delay(800, CancellationToken.None);
            Close();
        }
        catch
        {
            /* close silently if window was already gone */
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_copyDone || _closeConfirmed)
            return;
        e.Cancel = true;
        _ = ConfirmCancelAsync();
    }

    private async Task ConfirmCancelAsync()
    {
        var dialog = new AppDialog(
            "Cancel Import",
            "The import is not complete.\n\nCancel and delete the already copied files?",
            "Yes, Cancel",
            "No, Continue");
        await dialog.ShowDialog(this);
        if (dialog.Result != "Yes, Cancel")
            return;
        _closeConfirmed = true;
        await _cts.CancelAsync();
        Close();
    }

    private void AddFileRow(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.Parse("#3DD68C")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        var label = new TextBlock
        {
            Text = string.IsNullOrEmpty(fileName) ? relativePath : fileName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        if (this.TryFindResource("AppSubTextBrush", out var brush) && brush is IBrush b)
            label.Foreground = b;

        if (relativePath != fileName)
            ToolTip.SetTip(label, relativePath);

        var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        rowGrid.Children.Add(dot);
        Grid.SetColumn(label, 1);
        rowGrid.Children.Add(label);

        var row = new Border
        {
            Opacity = 0,
            Padding = new Thickness(4, 2),
            Child = rowGrid,
            Classes = { "fileRow" }
        };

        FileList.Children.Add(row);

        Dispatcher.UIThread.Post(() =>
        {
            row.Opacity = 1;
            FileScrollViewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }
}