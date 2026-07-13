using System.Collections.ObjectModel;
using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Apportia.Views;

public sealed record TerminateRow(RunningAppsService.KillCandidate Source)
{
    public string PidText => Source.Pid.ToString();
    public string Exe => Source.Exe;
    public string CommandLine => Source.CommandLine;
    public string StartedText => Source.StartTime is { } t ? t.ToString("HH:mm:ss") : "\u2014";
}

public sealed class TerminateGroup(string appName, Bitmap? icon, IEnumerable<TerminateRow> rows)
{
    public string AppName { get; } = appName;
    public Bitmap? Icon { get; } = icon;
    public ObservableCollection<TerminateRow> Rows { get; } = new(rows);
}

public sealed record TerminateGroupInput(string AppName, Bitmap? Icon, IReadOnlyList<RunningAppsService.KillCandidate> Candidates);

public partial class TerminateDialog : Window
{
    private readonly string _appName;
    private readonly ObservableCollection<TerminateGroup> _groups = [];

    public TerminateDialog() : this("", [])
    {
    }

    public TerminateDialog(string appName, IReadOnlyList<TerminateGroupInput> groups)
    {
        InitializeComponent();
        _appName = appName;
        Title = string.Format(UiText.Dialog.TerminateTitleFormat, appName);
        foreach (var g in groups.OrderBy(g => g.AppName, StringComparer.CurrentCultureIgnoreCase))
        {
            var rows = g.Candidates
                        .OrderByDescending(c => c.StartTime ?? DateTime.MinValue)
                        .Select(c => new TerminateRow(c));
            _groups.Add(new TerminateGroup(g.AppName, g.Icon, rows));
        }

        GroupList.ItemsSource = _groups;
        UpdateHeader();
    }

    public IReadOnlyList<int> RemainingPids =>
        _groups.SelectMany(g => g.Rows.Select(r => r.Source.Pid)).ToList();

    public bool Confirmed { get; private set; }

    private int TotalRowCount => _groups.Sum(g => g.Rows.Count);

    private void UpdateHeader()
    {
        var count = TotalRowCount;
        HeaderText.Text = count == 1
            ? string.Format(UiText.Dialog.TerminateHeaderSingleFormat, _appName)
            : string.Format(UiText.Dialog.TerminateHeaderMultipleFormat, count, _appName);
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
        var group = _groups.FirstOrDefault(g => g.Rows.Contains(row));
        group?.Rows.Remove(row);
        if (group is { Rows.Count: 0 })
            _groups.Remove(group);
        FinalizeAfterMutation();
    }

    private void OnTerminateGroup(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TerminateGroup group })
            return;
        RunningAppsService.KillPids(group.Rows.Select(r => r.Source.Pid).ToList());
        _groups.Remove(group);
        FinalizeAfterMutation();
    }

    private void FinalizeAfterMutation()
    {
        if (_groups.Count == 0)
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