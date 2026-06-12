using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class JavaRequiredDialog : Window
{
    public JavaRequiredDialog()
    {
        InitializeComponent();
    }

    public JavaRequiredDialog(string appName, string[] candidateNames) : this()
    {
        AppLabel.Text = appName;
        JavaList.ItemsSource = candidateNames;
        if (candidateNames.Length > 0)
            JavaList.SelectedIndex = 0;
    }

    public int[]? SelectedIndices { get; private set; }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var selected = JavaList.SelectedItems;
        if (selected == null || selected.Count == 0)
            return;
        if (JavaList.ItemsSource is not string[] source)
            return;
        SelectedIndices = selected.Cast<string>()
                                  .Select(s => Array.IndexOf(source, s))
                                  .Where(i => i >= 0)
                                  .ToArray();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
