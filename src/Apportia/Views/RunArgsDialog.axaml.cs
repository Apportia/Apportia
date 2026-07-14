using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Apportia.Platform;
using Apportia.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Apportia.Views;

public sealed class ArgItem(string value) : INotifyPropertyChanged
{
    private string _value = value;

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

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
        ArgsList.ItemsSource = Args;
        Args.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ArgItem item in e.NewItems)
                    item.PropertyChanged += OnArgValueChanged;
            if (e.OldItems != null)
                foreach (ArgItem item in e.OldItems)
                    item.PropertyChanged -= OnArgValueChanged;
            UpdateRunButtons();
        };
    }

    public RunArgsDialog(string appName, string[] prefilledArgs) : this()
    {
        RunAsAdminButton.IsVisible = OperatingSystem.IsWindows();
        AppLabel.Text = appName;
        foreach (var arg in prefilledArgs)
            Args.Add(new ArgItem(arg));

        if (Args.Count == 0)
            Args.Add(new ArgItem(string.Empty));
    }

    public ObservableCollection<ArgItem> Args { get; } = new();

    public RunChoice Choice { get; private set; } = RunChoice.Cancel;

    public string[] ArgsArray =>
        Args.Select(a => a.Value.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();

    private void OnArgValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateRunButtons();
    }

    private void OnAddArg(object? sender, RoutedEventArgs e)
    {
        if (!Args.Any(a => string.IsNullOrWhiteSpace(a.Value)))
            Args.Add(new ArgItem(string.Empty));
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
                Args.Add(new ArgItem(f.TryGetLocalPath() ?? f.Path.LocalPath));
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
                Args.Add(new ArgItem(d.TryGetLocalPath() ?? d.Path.LocalPath));
        }
        catch
        {
            /* folder picker failed – no folders added */
        }
    }

    private void RemoveEmptyRows()
    {
        for (var i = Args.Count - 1; i >= 0; i--)
            if (string.IsNullOrWhiteSpace(Args[i].Value))
                Args.RemoveAt(i);
    }

    private void OnRemoveArg(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ArgItem item }) return;
        if (Args.Count <= 1)
            item.Value = string.Empty;
        else
            Args.Remove(item);
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
