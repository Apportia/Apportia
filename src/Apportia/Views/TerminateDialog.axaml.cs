using Apportia.Platform;
using Apportia.Services;
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
    public TerminateDialog() : this("", [])
    {
    }

    public TerminateDialog(string appName, IReadOnlyList<RunningAppsService.KillCandidate> candidates)
    {
        InitializeComponent();
        Title = $"Terminate {appName}";
        HeaderText.Text = candidates.Count == 1
            ? $"The following process from {appName} will be terminated:"
            : $"The following {candidates.Count} processes from {appName} will be terminated:";
        ProcessList.ItemsSource = candidates.Select(c => new TerminateRow(c)).ToList();
    }

    public bool Confirmed { get; private set; }

    private void OnTerminate(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }
}