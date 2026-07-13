using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Apportia.Views;

public sealed class TerminateRow(RunningAppsService.KillCandidate source) : INotifyPropertyChanged
{
    private string _cpuText = "\u2014";
    private string _ramText = "\u2014";

    public RunningAppsService.KillCandidate Source { get; } = source;
    public string PidText => Source.Pid.ToString();
    public string Exe => Source.Exe;
    public string CommandLine => Source.CommandLine;
    public string StartedText => Source.StartTime is { } t ? t.ToString("HH:mm:ss") : "\u2014";

    public string CpuText
    {
        get => _cpuText;
        set
        {
            if (_cpuText == value)
                return;
            _cpuText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CpuText)));
        }
    }

    public string RamText
    {
        get => _ramText;
        set
        {
            if (_ramText == value)
                return;
            _ramText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RamText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TerminateGroup(string sectionName, string appName, Bitmap? icon, IEnumerable<TerminateRow> rows)
{
    public string SectionName { get; } = sectionName;
    public string AppName { get; } = appName;
    public Bitmap? Icon { get; } = icon;
    public ObservableCollection<TerminateRow> Rows { get; } = new(rows);
}

public sealed record TerminateGroupInput(string SectionName, string AppName, Bitmap? Icon);

public partial class TerminateDialog : Window
{
    private static readonly int ProcessorCount = Math.Max(1, Environment.ProcessorCount);
    private static readonly IComparer<string> AppNameComparer = StringComparer.CurrentCultureIgnoreCase;

    private readonly string _appName;
    private readonly ObservableCollection<TerminateGroup> _groups = [];
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime When)> _lastSample = [];
    private readonly Func<IReadOnlyList<TerminateGroupInput>> _sourceProvider;
    private DispatcherTimer? _sampleTimer;

    public TerminateDialog() : this("", () => [])
    {
    }

    public TerminateDialog(string appName, Func<IReadOnlyList<TerminateGroupInput>> sourceProvider)
    {
        InitializeComponent();
        _appName = appName;
        _sourceProvider = sourceProvider;
        Title = string.Format(UiText.Dialog.TerminateTitleFormat, appName);
        GroupList.ItemsSource = _groups;
        Refresh();
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
        Refresh();
    }

    private void OnTerminateGroup(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TerminateGroup group })
            return;
        RunningAppsService.KillPids(group.Rows.Select(r => r.Source.Pid).ToList());
        Refresh();
    }

    private void Refresh()
    {
        ReconcileGroups();
        SampleUsage();
        if (_groups.Count == 0)
        {
            Confirmed = false;
            Close();
            return;
        }

        UpdateHeader();
    }

    private void ReconcileGroups()
    {
        var inputs = _sourceProvider();
        var inputByKey = new Dictionary<string, TerminateGroupInput>(StringComparer.OrdinalIgnoreCase);
        var candidatesByKey = new Dictionary<string, IReadOnlyList<RunningAppsService.KillCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            var c = RunningAppsService.GetKillCandidates(input.SectionName);
            if (c.Count == 0)
                continue;
            inputByKey[input.SectionName] = input;
            candidatesByKey[input.SectionName] = c;
        }

        for (var i = _groups.Count - 1; i >= 0; i--)
            if (!inputByKey.ContainsKey(_groups[i].SectionName))
            {
                foreach (var row in _groups[i].Rows)
                    _lastSample.Remove(row.Source.Pid);
                _groups.RemoveAt(i);
            }

        foreach (var (key, input) in inputByKey)
        {
            var candidates = candidatesByKey[key];
            var existing = _groups.FirstOrDefault(g => string.Equals(g.SectionName, key, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                InsertGroupSorted(new TerminateGroup(input.SectionName, input.AppName, input.Icon,
                                                     candidates.OrderByDescending(c => c.StartTime ?? DateTime.MinValue).Select(c => new TerminateRow(c))));
            else
                ReconcileRows(existing, candidates);
        }
    }

    private void InsertGroupSorted(TerminateGroup group)
    {
        var idx = 0;
        while (idx < _groups.Count && AppNameComparer.Compare(_groups[idx].AppName, group.AppName) <= 0)
            idx++;
        _groups.Insert(idx, group);
    }

    private void ReconcileRows(TerminateGroup group, IReadOnlyList<RunningAppsService.KillCandidate> candidates)
    {
        var candidateByPid = candidates.ToDictionary(c => c.Pid);
        for (var i = group.Rows.Count - 1; i >= 0; i--)
            if (!candidateByPid.ContainsKey(group.Rows[i].Source.Pid))
            {
                _lastSample.Remove(group.Rows[i].Source.Pid);
                group.Rows.RemoveAt(i);
            }

        var existingPids = group.Rows.Select(r => r.Source.Pid).ToHashSet();
        foreach (var c in candidates.OrderByDescending(c => c.StartTime ?? DateTime.MinValue))
            if (existingPids.Add(c.Pid))
                InsertRowSorted(group, new TerminateRow(c));
    }

    private static void InsertRowSorted(TerminateGroup group, TerminateRow row)
    {
        var rowStart = row.Source.StartTime ?? DateTime.MinValue;
        var idx = 0;
        while (idx < group.Rows.Count &&
               (group.Rows[idx].Source.StartTime ?? DateTime.MinValue) >= rowStart)
            idx++;
        group.Rows.Insert(idx, row);
    }

    private void SampleUsage()
    {
        var now = DateTime.UtcNow;
        foreach (var group in _groups)
        {
            foreach (var row in group.Rows)
            {
                var pid = row.Source.Pid;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.HasExited)
                        continue;
                    var cpu = p.TotalProcessorTime;
                    var ram = p.WorkingSet64;
                    if (_lastSample.TryGetValue(pid, out var prev))
                    {
                        var elapsedMs = (now - prev.When).TotalMilliseconds;
                        if (elapsedMs > 0)
                        {
                            var cpuMs = (cpu - prev.Cpu).TotalMilliseconds;
                            var percent = cpuMs / (elapsedMs * ProcessorCount) * 100.0;
                            if (percent < 0)
                                percent = 0;
                            row.CpuText = percent.ToString("0.0") + " %";
                        }
                    }

                    row.RamText = AppDiskUsageService.FormatSize(ram);
                    _lastSample[pid] = (cpu, now);
                }
                catch
                {
                    /* process gone – Refresh will remove it next tick */
                }
            }
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
        _sampleTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh());
        _sampleTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _sampleTimer?.Stop();
        _sampleTimer = null;
        _lastSample.Clear();
        base.OnClosed(e);
    }
}