using Apportia.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class ExePickerDialog : Window
{
    public ExePickerDialog()
    {
        InitializeComponent();
    }

    public ExePickerDialog(string appName, string[] candidates) : this()
    {
        AppLabel.Text = appName;
        ExeList.ItemsSource = candidates;
        if (candidates.Length > 0)
            ExeList.SelectedIndex = 0;
    }

    public string? SelectedExe { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

    private void OnSelect(object? sender, RoutedEventArgs e)
    {
        if (ExeList.SelectedItem is not string selected)
            return;
        SelectedExe = selected;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}