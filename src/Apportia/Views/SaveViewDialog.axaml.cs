using Apportia.Platform;
using Apportia.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public partial class SaveViewDialog : Window
{
    public enum Action
    {
        Save,
        Reset
    }

    public SaveViewDialog()
    {
        InitializeComponent();
    }

    public SaveViewDialog(InstallFilter activeFilter) : this()
    {
        AllAppsCheck.IsChecked = activeFilter == InstallFilter.All;
        InstalledCheck.IsChecked = activeFilter == InstallFilter.Installed;
        NotInstalledCheck.IsChecked = activeFilter == InstallFilter.NotInstalled;
    }

    public Action DialogAction { get; private set; } = Action.Save;
    public IReadOnlyList<InstallFilter> SelectedFilters { get; private set; } = [];

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        DialogAction = Action.Save;
        SelectedFilters = CollectChecked();
        Close();
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        DialogAction = Action.Reset;
        SelectedFilters = CollectChecked();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private List<InstallFilter> CollectChecked()
    {
        var filters = new List<InstallFilter>();
        if (AllAppsCheck.IsChecked == true)
            filters.Add(InstallFilter.All);
        if (InstalledCheck.IsChecked == true)
            filters.Add(InstallFilter.Installed);
        if (NotInstalledCheck.IsChecked == true)
            filters.Add(InstallFilter.NotInstalled);
        return filters;
    }
}