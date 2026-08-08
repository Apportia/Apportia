using Apportia.Text;

namespace Apportia.Services;

public static class RelativeDate
{
    public static string Format(string? raw)
    {
        return !DateTime.TryParse(raw, out var date) ? raw ?? string.Empty : Format(date);
    }

    public static string Format(DateTime date)
    {
        return FormatCore(date, true);
    }

    public static string FormatShort(string? raw)
    {
        return !DateTime.TryParse(raw, out var date) ? raw ?? string.Empty : FormatShort(date);
    }

    public static string FormatShort(DateTime date)
    {
        return FormatCore(date, false);
    }

    public static string FormatShortOrNever(DateTime? date)
    {
        return date is null ? UiText.Header.RelNever : FormatShort(date.Value);
    }

    private static string FormatCore(DateTime date, bool longForm)
    {
        var now = DateTime.Now;
        var delta = now - date;
        var hasTime = date.TimeOfDay != TimeSpan.Zero;

        if (delta.Ticks < 0)
            return date.ToString(longForm ? "dddd, MMMM d, yyyy" : "MMM d, yyyy");

        if (hasTime && delta.TotalHours < 24)
            return FormatSubDay(delta, longForm);

        var days = hasTime
            ? (int)delta.TotalDays
            : (DateTime.Today - date.Date).Days;
        var dayName = date.ToString("dddd");
        return days switch
        {
            0 => longForm ? $"{dayName}, {UiText.Header.RelToday}" : UiText.Header.RelToday,
            1 => FormatOneDay(hasTime, dayName, longForm),
            <= 6 => longForm
                ? string.Format(UiText.Header.RelDaysAgoFormat, dayName, days)
                : string.Format(UiText.Header.RelDaysAgoShortFormat, days),
            7 => longForm ? $"{dayName}, {UiText.Header.RelWeekAgo}" : UiText.Header.RelWeekAgo,
            _ => date.ToString(longForm ? "dddd, MMMM d, yyyy" : "MMM d, yyyy")
        };
    }

    private static string FormatSubDay(TimeSpan delta, bool longForm)
    {
        if (delta.TotalSeconds < 60)
        {
            var s = Math.Max(0, (int)delta.TotalSeconds);
            if (s <= 1)
                return longForm ? UiText.Header.RelSecondAgo : UiText.Header.RelSecondAgoShort;
            return string.Format(
                longForm ? UiText.Header.RelSecondsAgoFormat : UiText.Header.RelSecondsAgoShortFormat,
                s);
        }

        if (delta.TotalMinutes < 60)
        {
            var m = (int)delta.TotalMinutes;
            if (m == 1)
                return longForm ? UiText.Header.RelMinuteAgo : UiText.Header.RelMinuteAgoShort;
            return string.Format(
                longForm ? UiText.Header.RelMinutesAgoFormat : UiText.Header.RelMinutesAgoShortFormat,
                m);
        }

        var h = (int)delta.TotalHours;
        return h == 1
            ? UiText.Header.RelHourAgo
            : string.Format(UiText.Header.RelHoursAgoFormat, h);
    }

    private static string FormatOneDay(bool hasTime, string dayName, bool longForm)
    {
        var label = hasTime ? UiText.Header.RelDayAgo : UiText.Header.RelYesterday;
        return longForm ? $"{dayName}, {label}" : label;
    }
}