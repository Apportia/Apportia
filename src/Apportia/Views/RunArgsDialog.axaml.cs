using Apportia.Platform;
using Apportia.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Apportia.Views;

public partial class RunArgsDialog : Window
{
    public enum RunChoice
    {
        WithArgs,
        WithArgsAsAdmin,
        WithoutArgs,
        Cancel
    }

    public RunArgsDialog()
    {
        InitializeComponent();
    }

    public RunArgsDialog(string appName, string[] prefilledArgs) : this()
    {
        RunAsAdminButton.IsVisible = OperatingSystem.IsWindows();
        AppLabel.Text = appName;
        foreach (var arg in prefilledArgs)
            AddArgRow(arg);

        if (ArgsList.Children.Count == 0)
            AddArgRow(string.Empty);
    }

    public RunChoice Choice { get; private set; } = RunChoice.Cancel;

    public string[] ArgsArray =>
        ArgsList.Children
                .OfType<Grid>()
                .Select(g => (g.Children[0] as TextBox)?.Text?.Trim() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

    private void OnAddArg(object? sender, RoutedEventArgs e)
    {
        var hasEmpty =
            ArgsList.Children
                    .OfType<Grid>()
                    .Any(g => string.IsNullOrWhiteSpace((g.Children[0] as TextBox)?.Text));
        if (!hasEmpty)
            AddArgRow(string.Empty);
    }

    private async void OnAddFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiText.Dialog.RunArgsFilePickerTitle,
                AllowMultiple = true
            });
            if (files.Count == 0)
                return;
            RemoveEmptyRows();
            foreach (var f in files)
                AddArgRow(f.TryGetLocalPath() ?? f.Path.LocalPath);
        }
        catch
        {
            /* file picker failed – no files added */
        }
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiText.Dialog.RunArgsFolderPickerTitle,
                AllowMultiple = true
            });
            if (folders.Count == 0)
                return;
            RemoveEmptyRows();
            foreach (var d in folders)
                AddArgRow(d.TryGetLocalPath() ?? d.Path.LocalPath);
        }
        catch
        {
            /* folder picker failed – no folders added */
        }
    }

    private void RemoveEmptyRows()
    {
        var empty =
            ArgsList.Children
                    .OfType<Grid>()
                    .Where(g => string.IsNullOrWhiteSpace((g.Children[0] as TextBox)?.Text))
                    .ToList();
        foreach (var row in empty)
            ArgsList.Children.Remove(row);
    }

    private void AddArgRow(string value)
    {
        var textBox = new TextBox
        {
            Text = value,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var removeBtn = new Button
        {
            Content = UiText.Button.RemoveArg,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        row.Children.Add(textBox);
        Grid.SetColumn(removeBtn, 1);
        row.Children.Add(removeBtn);

        textBox.TextChanged += (_, _) => UpdateRunButtons();
        removeBtn.Click += (_, _) =>
        {
            ArgsList.Children.Remove(row);
            UpdateRunButtons();
        };

        ArgsList.Children.Add(row);
        UpdateRunButtons();
    }

    private void UpdateRunButtons()
    {
        var hasArgs = ArgsArray.Length > 0;
        RunButton.IsEnabled = hasArgs;
        RunAsAdminButton.IsEnabled = hasArgs;
    }

    private void OnRunWithArgs(object? sender, RoutedEventArgs e)
    {
        Choice = RunChoice.WithArgs;
        Close();
    }

    private void OnRunAsAdmin(object? sender, RoutedEventArgs e)
    {
        Choice = RunChoice.WithArgsAsAdmin;
        Close();
    }

    private void OnRunWithoutArgs(object? sender, RoutedEventArgs e)
    {
        Choice = RunChoice.WithoutArgs;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    // Joins args back into a string; wraps in quotes if the arg contains spaces.
    public static string CombineArgs(IEnumerable<string> args)
    {
        return string.Join(
            " ",
            args.Select(a => a.Contains(' ') && !a.StartsWith('"') && !a.StartsWith('\'')
                            ? $"\"{a}\""
                            : a));
    }
}