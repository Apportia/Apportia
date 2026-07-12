using System.Collections.ObjectModel;
using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Apportia.Views;

public sealed record TerminateRow(RunningAppsService.KillCandidate Source)
{
    public string PidText => Source.Pid.ToString();
    public string Exe => Source.Exe;
    public string CommandLine => Source.CommandLine;
    public string StartedText => Source.StartTime is { } t ? t.ToString("HH:mm:ss") : "\u2014";
}

public partial class TerminateDialog : Window
{
    private readonly string _appName;
    private readonly ObservableCollection<TerminateRow> _rows = [];

    public TerminateDialog() : this("", [])
    {
    }

    public TerminateDialog(string appName, IReadOnlyList<RunningAppsService.KillCandidate> candidates)
    {
        InitializeComponent();
        _appName = appName;
        Title = string.Format(UiText.Dialog.TerminateTitleFormat, appName);
        foreach (var c in candidates)
            _rows.Add(new TerminateRow(c));
        ProcessList.ItemsSource = _rows;
        UpdateHeader();
    }

    public IReadOnlyList<int> RemainingPids => _rows.Select(r => r.Source.Pid).ToList();
    public bool Confirmed { get; private set; }

    private void UpdateHeader()
    {
        HeaderText.Text = _rows.Count == 1
            ? string.Format(UiText.Dialog.TerminateHeaderSingleFormat, _appName)
            : string.Format(UiText.Dialog.TerminateHeaderMultipleFormat, _rows.Count, _appName);
    }

    private void OnTerminate(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnTerminateRow(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TerminateRow row })
            return;
        RunningAppsService.KillPids([row.Source.Pid]);
        _rows.Remove(row);
        if (_rows.Count == 0)
        {
            Confirmed = false;
            Close();
            return;
        }

        UpdateHeader();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }
}