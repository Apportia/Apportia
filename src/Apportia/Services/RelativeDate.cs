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
        var days = (DateTime.Today - date.Date).Days;
        var dayName = date.ToString("dddd");
        if (days < 0)
            return date.ToString("dddd, MMMM d, yyyy");
        return days switch
        {
            0 => $"{dayName}, {UiText.Header.RelToday}",
            1 => $"{dayName}, {UiText.Header.RelYesterday}",
            <= 6 => string.Format(UiText.Header.RelDaysAgoFormat, dayName, days),
            7 => $"{dayName}, {UiText.Header.RelWeekAgo}",
            _ => date.ToString("dddd, MMMM d, yyyy")
        };
    }

    public static string FormatShort(string? raw)
    {
        return !DateTime.TryParse(raw, out var date) ? raw ?? string.Empty : FormatShort(date);
    }

    public static string FormatShort(DateTime date)
    {
        var days = (DateTime.Today - date.Date).Days;
        if (days < 0)
            return date.ToString("MMM d, yyyy");
        return days switch
        {
            0 => UiText.Header.RelToday,
            1 => UiText.Header.RelYesterday,
            <= 6 => string.Format(UiText.Header.RelDaysAgoShortFormat, days),
            7 => UiText.Header.RelWeekAgo,
            _ => date.ToString("MMM d, yyyy")
        };
    }
}
