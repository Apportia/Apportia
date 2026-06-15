using Apportia.Platform;
using Apportia.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public sealed record LeftoverEntry(string Name, string Kind);

public partial class LeftoverFilesDialog : Window
{
    private readonly string[] _paths = [];

    public LeftoverFilesDialog()
    {
        InitializeComponent();
    }

    public LeftoverFilesDialog(IReadOnlyList<string> paths) : this()
    {
        _paths = [.. paths];
        ItemList.ItemsSource = paths.Select(p =>
        {
            var name = Path.GetFileName(p);
            var kind = Directory.Exists(p) ? "Folder" : "File";
            return new LeftoverEntry(name, kind);
        }).ToArray();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        foreach (var path in _paths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                SettingsService.Log($"Failed to delete leftover '{path}': {ex.Message}");
            }
        }

        if (errors.Count > 0)
            DeleteButton.IsEnabled = false;

        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
