using Avalonia.Threading;

namespace Apportia.Services;

public static class RelativeTimeTicker
{
    private static DispatcherTimer? _timer;
    private static EventHandler? _tick;

    public static event EventHandler Tick
    {
        add
        {
            _tick += value;
            _timer ??= StartTimer();
        }
        remove => _tick -= value;
    }

    private static DispatcherTimer StartTimer()
    {
        var t = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        t.Tick += (_, _) => _tick?.Invoke(null, EventArgs.Empty);
        t.Start();
        return t;
    }
}
